using System.Globalization;
using System.Text.RegularExpressions;
using OnCourse.ABConnect.Http;
using OnCourse.ABConnect.Models;

namespace OnCourse.ABConnect.Queries;

/// <summary>
/// Renders the immutable query types into AB Connect request URIs.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place a filter, field list, sort, or page window becomes text. Centralizing it is
/// what makes the escaping rule enforceable: each expression is passed through
/// <see cref="Uri.EscapeDataString"/> exactly once, never zero times and never twice.
/// </para>
/// <para>
/// The rules this type enforces, each of them the repair of a named defect:
/// every GUID is validated against <c>^[0-9A-Fa-f-]{32,36}$</c> before it reaches an expression;
/// filter paths always use the <c>.guid</c> spelling and a path ending in <c>.id</c> is rejected;
/// the status scope is always emitted, so deleted standards are visible by default;
/// the sort is always emitted, so paging never runs against relevance ordering;
/// the limit is clamped to <see cref="PageRequest.MaxLimit"/> and lowered to
/// <see cref="ABConnectOptions.PageSize"/>; and <c>*</c> is rejected outright unless
/// <see cref="ABConnectOptions.AllowWildcardFields"/> is set.
/// </para>
/// <para>
/// Every returned URI is relative to <see cref="ABConnectOptions.BaseAddress"/> and carries no
/// credentials. It is therefore safe to log, and it is exactly the string recorded as
/// <see cref="Http.ABConnectRequestContext.RedactedPath"/>.
/// </para>
/// </remarks>
public static partial class ABQueryStringBuilder
{
    /// <summary>The AB Connect resource path for standards, both lists and lookups by GUID.</summary>
    public const string StandardsResource = "standards";

    /// <summary>The AB Connect resource path for change events.</summary>
    public const string EventsResource = "events";

    /// <summary>The AB Connect query parameter that requests a facet summary instead of rows.</summary>
    public const string FacetSummaryParameter = "facet_summary";

    private const string StandardsScope = "standards";
    private const string EventsScope = "events";
    private const string StatusField = "status";
    private const string SequenceField = "seq";

    /// <summary>
    /// The limit a facet request always carries. Zero rows, because the caller wants the facet
    /// summary and not the standards it was computed over.
    /// </summary>
    private const string FacetLimit = "limit=0";

    /// <summary>Renders a standards query into a relative request URI and its request context.</summary>
    /// <remarks>
    /// The emitted query always carries <c>fields[standards]</c>, <c>filter[standards]</c>,
    /// <c>sort[standards]</c>, <c>limit</c>, and <c>offset</c>, in that order. The filter always
    /// contains a <c>status</c> term, so a caller cannot accidentally issue the status-less query
    /// that hides every deleted standard.
    /// </remarks>
    /// <param name="query">The query to render.</param>
    /// <param name="options">The options supplying the page size and the wildcard permission.</param>
    /// <returns>The relative URI to request and the context describing it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">A field name, sort key, or filter path is not a well-formed AB Connect property path, or a filter value is not a well-formed GUID.</exception>
    /// <exception cref="ABConnectConfigurationException">The query uses a wildcard field set while <see cref="ABConnectOptions.AllowWildcardFields"/> is false.</exception>
    public static (string RequestUri, ABConnectRequestContext Context) Build(StandardsQuery query, ABConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);

        bool isWildcard = RequireWildcardPermitted(
            query.Fields.Fields,
            options,
            StandardsScope,
            $"{nameof(StandardFieldSet)}.{nameof(StandardFieldSet.Snapshot)}");

        string[] parameters =
        [
            $"fields[{StandardsScope}]={RenderTokens(query.Fields.Fields, nameof(query))}",
            $"filter[{StandardsScope}]={Uri.EscapeDataString(RenderStandardsFilter(query.Filter, query.Status, nameof(query)))}",
            $"sort[{StandardsScope}]={RenderTokens(query.Sort.Keys, nameof(query))}",
            RenderLimit(query.Page, options),
            RenderOffset(query.Page),
        ];

        return Complete(StandardsResource, parameters, isWildcard);
    }

    /// <summary>Renders an events query into a relative request URI and its request context.</summary>
    /// <remarks>
    /// The emitted query always carries <c>sort[events]=seq</c>. AB Connect orders list results by
    /// relevance by default, so ascending sequence order has to be requested on every single call; a
    /// delta pull that omits it advances its watermark past events it never saw. The watermark itself
    /// is emitted as <c>filter[events]=(seq GT n)</c>, and
    /// <see cref="EventsQuery.StandardScope"/> is conjoined with it when present.
    /// </remarks>
    /// <param name="query">The query to render.</param>
    /// <param name="options">The options supplying the page size.</param>
    /// <returns>The relative URI to request and the context describing it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">A field name or filter path is not a well-formed AB Connect property path, or a filter value is not a well-formed GUID.</exception>
    /// <exception cref="ABConnectConfigurationException">The query uses a wildcard field set while <see cref="ABConnectOptions.AllowWildcardFields"/> is false.</exception>
    public static (string RequestUri, ABConnectRequestContext Context) Build(EventsQuery query, ABConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);

        bool isWildcard = RequireWildcardPermitted(
            query.Fields.Fields,
            options,
            EventsScope,
            $"{nameof(EventFieldSet)}.{nameof(EventFieldSet.Full)}");

        string[] parameters =
        [
            $"fields[{EventsScope}]={RenderTokens(query.Fields.Fields, nameof(query))}",
            $"filter[{EventsScope}]={Uri.EscapeDataString(RenderEventsFilter(query, nameof(query)))}",
            $"sort[{EventsScope}]={SequenceField}",
            RenderLimit(query.Page, options),
            RenderOffset(query.Page),
        ];

        return Complete(EventsResource, parameters, isWildcard);
    }

    /// <summary>Renders a facet query into a relative request URI and its request context.</summary>
    /// <remarks>
    /// A facet request is a standards request with <c>limit=0</c> and a <c>facet_summary</c>
    /// parameter, and it is issued exactly once: AB Connect does not support paging of facet data, so
    /// there is no offset to advance and no second call to make. No <c>status</c> term is emitted,
    /// because <see cref="FacetQuery{TValue}"/> does not model a status scope; AB Connect's own
    /// default therefore applies and the facet is computed over standards that are not deleted, which
    /// is what a caller enumerating currently published documents, publications, and sections wants.
    /// </remarks>
    /// <typeparam name="TValue">The detail type each facet value deserializes into.</typeparam>
    /// <param name="query">The query to render.</param>
    /// <param name="options">The options supplying the wildcard permission.</param>
    /// <returns>The relative URI to request and the context describing it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException"><see cref="FacetQuery{TValue}.FacetName"/> is empty or is not a well-formed AB Connect property path, or a filter path or value is invalid.</exception>
    /// <exception cref="ABConnectConfigurationException">The facet name is the wildcard token while <see cref="ABConnectOptions.AllowWildcardFields"/> is false.</exception>
    public static (string RequestUri, ABConnectRequestContext Context) Build<TValue>(FacetQuery<TValue> query, ABConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);

        string facetName = ValidateToken(query.FacetName, nameof(query));
        bool isWildcard = facetName == StandardFieldSet.WildcardToken;
        if (isWildcard && !options.AllowWildcardFields)
        {
            throw new ABConnectConfigurationException(
                $"A facet summary of '{StandardFieldSet.WildcardToken}' requests every facet AB Connect knows about and is throttled to two requests per second. Set {ABConnectOptions.SectionName}:{nameof(ABConnectOptions.AllowWildcardFields)} to true to permit it, which is intended for discovery only.");
        }

        List<string> parameters =
        [
            $"{FacetSummaryParameter}={facetName}",
        ];

        StandardsFilter filter = query.Filter ?? StandardsFilter.None;
        if (!filter.IsEmpty)
        {
            parameters.Add($"filter[{StandardsScope}]={Uri.EscapeDataString(RenderTerms(filter, nameof(query)))}");
        }

        parameters.Add(FacetLimit);

        return Complete(StandardsResource, parameters, isWildcard);
    }

    /// <summary>Renders a lookup of a single standard by GUID into a relative request URI and its request context.</summary>
    /// <remarks>
    /// A lookup carries no filter, no sort, and no page window. AB Connect returns a standard from
    /// this route whether it is active or deleted, which is why no status scope is expressed here.
    /// </remarks>
    /// <param name="guid">The AB Connect GUID of the standard.</param>
    /// <param name="fields">The field set to request, or null for <see cref="StandardFieldSet.Snapshot"/>.</param>
    /// <param name="options">The options supplying the wildcard permission.</param>
    /// <returns>The relative URI to request and the context describing it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="guid"/> is empty or is not a well-formed GUID, or a field name is not a well-formed AB Connect property path.</exception>
    /// <exception cref="ABConnectConfigurationException">The lookup uses a wildcard field set while <see cref="ABConnectOptions.AllowWildcardFields"/> is false.</exception>
    public static (string RequestUri, ABConnectRequestContext Context) BuildStandardLookup(string guid, StandardFieldSet? fields, ABConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string validated = StandardsFilter.ValidateGuid(guid, nameof(guid));
        StandardFieldSet fieldSet = fields ?? StandardFieldSet.Snapshot;
        bool isWildcard = RequireWildcardPermitted(
            fieldSet.Fields,
            options,
            StandardsScope,
            $"{nameof(StandardFieldSet)}.{nameof(StandardFieldSet.Snapshot)}");

        string[] parameters =
        [
            $"fields[{StandardsScope}]={RenderTokens(fieldSet.Fields, nameof(fields))}",
        ];

        return Complete($"{StandardsResource}/{validated}", parameters, isWildcard);
    }

    private static (string RequestUri, ABConnectRequestContext Context) Complete(
        string resource,
        IReadOnlyList<string> parameters,
        bool isWildcard)
    {
        string requestUri = $"{resource}?{string.Join('&', parameters)}";
        return (requestUri, new ABConnectRequestContext(requestUri) { IsWildcardRequest = isWildcard });
    }

    /// <summary>
    /// Rejects a wildcard field set unless it has been explicitly permitted, and reports whether the
    /// request is a wildcard request so the throttle handler can charge the narrower bucket.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The check looks for the wildcard token anywhere in the set rather than relying on
    /// <see cref="StandardFieldSet.IsWildcard"/>, so a hand-built set such as
    /// <c>StandardFieldSet.Of("guid", "*")</c> is gated too.
    /// </para>
    /// <para>
    /// It applies to <c>fields[events]</c> exactly as it applies to <c>fields[standards]</c>. AB
    /// Connect throttles the wildcard by the value of the <c>fields</c> parameter, not by the resource
    /// it was asked of, so an events query built from <c>EventFieldSet.Of("*")</c> costs the same two
    /// requests per second and has to pass the same gate.
    /// </para>
    /// </remarks>
    /// <param name="fields">The field names the query will emit.</param>
    /// <param name="options">The options carrying the wildcard permission.</param>
    /// <param name="scope">The <c>fields[...]</c> scope, <c>standards</c> or <c>events</c>, named in the failure message.</param>
    /// <param name="explicitSetName">The explicit field set to recommend instead, named in the failure message.</param>
    /// <returns>Whether the request is a wildcard request and must also spend a narrow-bucket token.</returns>
    /// <exception cref="ABConnectConfigurationException">
    /// The set contains the wildcard token while <see cref="ABConnectOptions.AllowWildcardFields"/> is
    /// false.
    /// </exception>
    private static bool RequireWildcardPermitted(
        IReadOnlyList<string> fields,
        ABConnectOptions options,
        string scope,
        string explicitSetName)
    {
        bool isWildcard = false;
        foreach (string field in fields)
        {
            if (field == StandardFieldSet.WildcardToken)
            {
                isWildcard = true;
                break;
            }
        }

        if (isWildcard && !options.AllowWildcardFields)
        {
            throw new ABConnectConfigurationException(
                $"A fields[{scope}] value of '{StandardFieldSet.WildcardToken}' asks AB Connect for every property of every row and is throttled to two requests per second rather than the account's five. Request an explicit field set such as {explicitSetName}, or set {ABConnectOptions.SectionName}:{nameof(ABConnectOptions.AllowWildcardFields)} to true to permit it for discovery.");
        }

        return isWildcard;
    }

    /// <summary>
    /// Renders the standards filter expression: the filter's own terms, then the status term, all
    /// conjoined.
    /// </summary>
    /// <remarks>
    /// The status term is unconditional. AB Connect excludes deleted standards from filter results
    /// unless the filter names them, so a query without a status term cannot see a deletion at all.
    /// </remarks>
    private static string RenderStandardsFilter(StandardsFilter filter, StandardStatusScope status, string parameterName)
    {
        List<string> terms = new(filter.Terms.Count + 1);
        AppendTerms(terms, filter, parameterName);
        terms.Add(RenderStatusTerm(status, parameterName));
        return Conjoin(terms);
    }

    /// <summary>Renders the events filter expression: the sequence watermark, then any standard scope.</summary>
    private static string RenderEventsFilter(EventsQuery query, string parameterName)
    {
        StandardsFilter scope = query.StandardScope ?? StandardsFilter.None;
        List<string> terms = new(scope.Terms.Count + 1)
        {
            $"({SequenceField} GT {query.AfterSequence.ToString(CultureInfo.InvariantCulture)})",
        };

        AppendTerms(terms, scope, parameterName);
        return Conjoin(terms);
    }

    /// <summary>Renders a standalone filter expression from a filter's terms, with no status term.</summary>
    private static string RenderTerms(StandardsFilter filter, string parameterName)
    {
        List<string> terms = new(filter.Terms.Count);
        AppendTerms(terms, filter, parameterName);
        return Conjoin(terms);
    }

    private static void AppendTerms(List<string> terms, StandardsFilter filter, string parameterName)
    {
        foreach (StandardsFilterTerm term in filter.Terms)
        {
            string field = ValidateFilterPath(term.Field, parameterName);
            string value = StandardsFilter.ValidateGuid(term.Value, parameterName);
            terms.Add($"({field} EQ '{value}')");
        }
    }

    /// <summary>
    /// Joins already-parenthesized terms with <c>AND</c>, adding an outer group only when there is
    /// more than one term, so a single-term expression renders exactly as AB Connect's own examples
    /// spell it.
    /// </summary>
    private static string Conjoin(List<string> terms)
        => terms.Count == 1 ? terms[0] : $"({string.Join(" AND ", terms)})";

    private static string RenderStatusTerm(StandardStatusScope status, string parameterName) => status switch
    {
        StandardStatusScope.Active => $"({StatusField} EQ '{ABStandardStatuses.Active}')",
        StandardStatusScope.Deleted => $"({StatusField} EQ '{ABStandardStatuses.Deleted}')",
        StandardStatusScope.Obsolete => $"({StatusField} EQ '{ABStandardStatuses.Obsolete}')",
        StandardStatusScope.ActiveAndDeleted => $"({StatusField} IN ('{ABStandardStatuses.Active}','{ABStandardStatuses.Deleted}'))",
        StandardStatusScope.All => $"({StatusField} IN ('{ABStandardStatuses.Active}','{ABStandardStatuses.Deleted}','{ABStandardStatuses.Obsolete}'))",
        _ => throw new ArgumentException(
            $"'{status}' is not a recognized {nameof(StandardStatusScope)}.",
            parameterName),
    };

    /// <summary>
    /// Renders the effective <c>limit</c>, which is the smaller of the page's own limit and the
    /// configured page size.
    /// </summary>
    /// <remarks>
    /// <see cref="PageRequest.First"/> carries <see cref="PageRequest.MaxLimit"/> because a static
    /// property cannot read configuration. Lowering it here is what makes
    /// <see cref="ABConnectOptions.PageSize"/> mean something, and taking the smaller of the two is
    /// what stops it from silently raising a limit a caller deliberately set low.
    /// </remarks>
    private static string RenderLimit(PageRequest page, ABConnectOptions options)
    {
        int configured = Math.Clamp(options.PageSize, 1, PageRequest.MaxLimit);
        int limit = Math.Min(page.Limit, configured);
        return $"limit={limit.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Renders the <c>offset</c>. A negative offset is impossible here: it is rejected by
    /// <see cref="PageRequest(int, int)"/> at construction, so no traversal can hand a sentinel back
    /// into a request.
    /// </summary>
    private static string RenderOffset(PageRequest page)
        => $"offset={page.Offset.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Renders a field list or sort key list as a single comma-separated value.
    /// </summary>
    /// <remarks>
    /// Each token is validated rather than escaped. Every character a valid AB Connect property path
    /// can contain is already safe in a query string, so escaping would only mangle the value: the
    /// wildcard token would become <c>%2A</c> and the separating commas <c>%2C</c>, neither of which
    /// appears in AB Connect's documented examples. A token that is not a well-formed path is
    /// rejected instead, which is the same guarantee by a different route.
    /// </remarks>
    private static string RenderTokens(IReadOnlyList<string> tokens, string parameterName)
    {
        if (tokens.Count == 0)
        {
            throw new ArgumentException("A field or sort list must contain at least one entry.", parameterName);
        }

        string[] validated = new string[tokens.Count];
        for (int index = 0; index < tokens.Count; index++)
        {
            validated[index] = ValidateToken(tokens[index], parameterName);
        }

        return string.Join(',', validated);
    }

    private static string ValidateToken(string token, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token, parameterName);

        if (!PropertyPathPattern().IsMatch(token))
        {
            throw new ArgumentException(
                $"'{token}' is not a well-formed AB Connect property path. Expected dot-separated names, optionally prefixed with '-' for a descending sort, or the wildcard '{StandardFieldSet.WildcardToken}'.",
                parameterName);
        }

        return token;
    }

    /// <summary>
    /// Validates a filter property path and enforces the <c>.guid</c> spelling.
    /// </summary>
    /// <remarks>
    /// AB Connect accepts <c>document.id</c> and <c>document.guid</c> interchangeably, verified live
    /// against the same document with identical <c>meta.count</c>. Accepting both spellings is what
    /// let one codebase filter on two different names for the same concept, so exactly one is
    /// permitted here and the other is a hard error.
    /// </remarks>
    private static string ValidateFilterPath(string path, string parameterName)
    {
        string validated = ValidateToken(path, parameterName);

        if (validated == "id" || validated.EndsWith(".id", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{validated}' uses the '.id' spelling. AB Connect treats it as equivalent to '.guid', and this SDK emits only '.guid' so that one concept has one spelling.",
                parameterName);
        }

        return validated;
    }

    [GeneratedRegex(@"^-?(\*|[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*)$", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyPathPattern();
}
