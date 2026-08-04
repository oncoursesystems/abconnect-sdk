using OnCourse.ABConnect.Models;
using OnCourse.ABConnect.Queries;
using OnCourse.ABConnect.Throttling;

namespace OnCourse.ABConnect.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IABConnectClient"/> so that <see cref="ABConnectFeed"/>'s paging, ordering,
/// and completeness logic is tested with no HTTP at all.
/// </summary>
/// <remarks>
/// <para>
/// Every call records the query object it was handed AND the relative request URI that
/// <see cref="ABQueryStringBuilder"/> renders from it. Recording the URI is what lets a paging test
/// assert on the wire form ("every request carries <c>sort[events]=seq</c>", "no request ever carries
/// <c>fields[standards]=*</c>") without standing up a transport. It also means the fake refuses any
/// query the real builder would refuse, so a traversal cannot pass a test by building a query that
/// could never be sent.
/// </para>
/// <para>
/// Responses are supplied through the <c>On*</c> delegates, which receive the query and the one-based
/// call number for that endpoint. An unconfigured endpoint throws rather than returning an empty
/// envelope, because a silently empty answer is the exact defect this SDK exists to kill.
/// </para>
/// </remarks>
public sealed class FakeABConnectClient : IABConnectClient
{
    private readonly ABConnectOptions _options;
    private readonly List<StandardsQuery> _standardsQueries = [];
    private readonly List<EventsQuery> _eventsQueries = [];
    private readonly List<FacetRequest> _facetRequests = [];
    private readonly List<string> _requestUris = [];
    private readonly List<string> _standardsRequestUris = [];
    private readonly List<string> _eventsRequestUris = [];

    /// <summary>Creates a fake whose URI rendering uses the default options.</summary>
    public FakeABConnectClient()
        : this(new ABConnectOptions())
    {
    }

    /// <summary>
    /// Creates a fake whose URI rendering uses the given options. Pass the same instance the feed under
    /// test was given, so the recorded URIs are the ones that would really have been sent.
    /// </summary>
    /// <param name="options">The options the query builder renders against.</param>
    public FakeABConnectClient(ABConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Every standards query handed to this client, in order.</summary>
    public IReadOnlyList<StandardsQuery> StandardsQueries => _standardsQueries;

    /// <summary>Every events query handed to this client, in order.</summary>
    public IReadOnlyList<EventsQuery> EventsQueries => _eventsQueries;

    /// <summary>Every facet request handed to this client, in order.</summary>
    public IReadOnlyList<FacetRequest> FacetRequests => _facetRequests;

    /// <summary>The relative request URI of every call, across all endpoints, in order.</summary>
    public IReadOnlyList<string> RequestUris => _requestUris;

    /// <summary>The relative request URI of every standards call, in order.</summary>
    public IReadOnlyList<string> StandardsRequestUris => _standardsRequestUris;

    /// <summary>The relative request URI of every events call, in order.</summary>
    public IReadOnlyList<string> EventsRequestUris => _eventsRequestUris;

    /// <summary>How many calls this client has served, across all endpoints.</summary>
    public int RequestCount => _requestUris.Count;

    /// <summary>
    /// Supplies the response to each standards call. The second argument is the one-based standards
    /// call number.
    /// </summary>
    public Func<StandardsQuery, int, ABPage<Standard>>? OnGetStandards { get; set; }

    /// <summary>
    /// Supplies the response to each events call. The second argument is the one-based events call
    /// number.
    /// </summary>
    public Func<EventsQuery, int, ABPage<ABEvent>>? OnGetEvents { get; set; }

    /// <summary>Supplies the response to each single-standard lookup.</summary>
    public Func<string, StandardFieldSet?, Standard>? OnGetStandard { get; set; }

    /// <summary>
    /// Optional canned facets, keyed by facet name. The value must be an
    /// <see cref="ABFacet{TValue}"/> of the type the call asks for. An unkeyed facet name yields a
    /// successful facet with zero values, which is the documented "answered, and there is none".
    /// </summary>
    public Dictionary<string, object> FacetResponses { get; } = [];

    /// <inheritdoc/>
    public IABConnectThrottleState Throttle { get; } = new InertThrottleState();

    /// <summary>Builds a standards page the way AB Connect shapes one.</summary>
    /// <param name="rows">The rows on the page.</param>
    /// <param name="reportedCount">The <c>meta.count</c> for the whole result set.</param>
    /// <param name="limit">The <c>meta.limit</c> AB Connect echoes, which the traversal's offset arithmetic follows.</param>
    /// <param name="offset">The <c>meta.offset</c> AB Connect echoes.</param>
    /// <returns>The page.</returns>
    public static ABPage<Standard> StandardsPage(
        IEnumerable<Standard> rows,
        int reportedCount,
        int limit = 100,
        int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return new ABPage<Standard>
        {
            Data = rows.ToList().AsReadOnly(),
            Meta = new PageMeta(limit, offset, reportedCount, 3),
            Links = new PageLinks(null, null, null, null, null),
        };
    }

    /// <summary>Builds an events page the way AB Connect shapes one.</summary>
    /// <param name="events">The events on the page.</param>
    /// <param name="reportedCount">The <c>meta.count</c> for the whole result set.</param>
    /// <param name="limit">The <c>meta.limit</c> AB Connect echoes.</param>
    /// <param name="offset">The <c>meta.offset</c> AB Connect echoes.</param>
    /// <returns>The page.</returns>
    public static ABPage<ABEvent> EventsPage(
        IEnumerable<ABEvent> events,
        int reportedCount,
        int limit = 100,
        int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(events);

        return new ABPage<ABEvent>
        {
            Data = events.ToList().AsReadOnly(),
            Meta = new PageMeta(limit, offset, reportedCount, 3),
            Links = new PageLinks(null, null, null, null, null),
        };
    }

    /// <summary>Builds a minimal standard row carrying an attributes GUID.</summary>
    /// <param name="guid">The standard's AB Connect GUID, used as both the resource id and <c>attributes.guid</c>.</param>
    /// <returns>The row.</returns>
    public static Standard StandardWithGuid(string guid)
        => new()
        {
            Id = guid,
            Type = "standards",
            Attributes = new StandardAttributes { Guid = guid, Status = ABStandardStatuses.Active },
        };

    /// <summary>Builds a minimal event carrying a sequence.</summary>
    /// <param name="seq">The event's sequence.</param>
    /// <returns>The event.</returns>
    public static ABEvent EventWithSequence(long seq)
        => new()
        {
            Id = seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Type = "events",
            Attributes = new ABEventAttributes
            {
                Seq = seq,
                ChangeType = ABChangeTypes.Added,
                Target = ABEventTargets.Standard,
            },
        };

    /// <summary>Builds an event with no attributes block, and therefore no sequence.</summary>
    /// <param name="id">The event's JSON:API resource id.</param>
    /// <returns>The event.</returns>
    public static ABEvent EventWithoutAttributes(string id)
        => new() { Id = id, Type = "events" };

    /// <summary>
    /// Reads one query-string parameter of a recorded request URI, still percent-encoded, so a test can
    /// assert on the exact wire form.
    /// </summary>
    /// <param name="requestUri">A recorded relative request URI.</param>
    /// <param name="name">The parameter name, for example <c>filter[events]</c>.</param>
    /// <returns>The raw, still-encoded value.</returns>
    /// <exception cref="InvalidOperationException">The URI carries no such parameter.</exception>
    public static string ParameterValue(string requestUri, string name)
    {
        ArgumentNullException.ThrowIfNull(requestUri);

        int separator = requestUri.IndexOf('?', StringComparison.Ordinal);
        string query = separator < 0 ? string.Empty : requestUri[(separator + 1)..];

        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            string key = equals < 0 ? pair : pair[..equals];

            if (string.Equals(key, name, StringComparison.Ordinal))
            {
                return equals < 0 ? string.Empty : pair[(equals + 1)..];
            }
        }

        throw new InvalidOperationException($"The request URI '{requestUri}' carries no '{name}' parameter.");
    }

    /// <summary>
    /// Reads one query-string parameter of a recorded request URI and unescapes it exactly once.
    /// </summary>
    /// <param name="requestUri">A recorded relative request URI.</param>
    /// <param name="name">The parameter name, for example <c>filter[standards]</c>.</param>
    /// <returns>The value after a single unescape pass.</returns>
    public static string DecodedParameterValue(string requestUri, string name)
        => Uri.UnescapeDataString(ParameterValue(requestUri, name));

    /// <inheritdoc/>
    public Task<ABPage<Standard>> GetStandardsAsync(
        StandardsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        string requestUri = ABQueryStringBuilder.Build(query, _options).RequestUri;
        _standardsQueries.Add(query);
        _standardsRequestUris.Add(requestUri);
        _requestUris.Add(requestUri);

        Func<StandardsQuery, int, ABPage<Standard>> responder = OnGetStandards
            ?? throw new InvalidOperationException(
                $"{nameof(FakeABConnectClient)}.{nameof(OnGetStandards)} was not configured, but the " +
                $"code under test issued standards request {_standardsQueries.Count}: {requestUri}");

        return Task.FromResult(responder(query, _standardsQueries.Count));
    }

    /// <inheritdoc/>
    public Task<Standard> GetStandardAsync(
        string guid,
        StandardFieldSet? fields = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string requestUri = ABQueryStringBuilder.BuildStandardLookup(guid, fields, _options).RequestUri;
        _requestUris.Add(requestUri);

        Func<string, StandardFieldSet?, Standard> responder = OnGetStandard
            ?? throw new InvalidOperationException(
                $"{nameof(FakeABConnectClient)}.{nameof(OnGetStandard)} was not configured, but the " +
                $"code under test issued the lookup {requestUri}");

        return Task.FromResult(responder(guid, fields));
    }

    /// <inheritdoc/>
    public Task<ABPage<ABEvent>> GetEventsAsync(
        EventsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        string requestUri = ABQueryStringBuilder.Build(query, _options).RequestUri;
        _eventsQueries.Add(query);
        _eventsRequestUris.Add(requestUri);
        _requestUris.Add(requestUri);

        Func<EventsQuery, int, ABPage<ABEvent>> responder = OnGetEvents
            ?? throw new InvalidOperationException(
                $"{nameof(FakeABConnectClient)}.{nameof(OnGetEvents)} was not configured, but the code " +
                $"under test issued events request {_eventsQueries.Count}: {requestUri}");

        return Task.FromResult(responder(query, _eventsQueries.Count));
    }

    /// <inheritdoc/>
    public Task<ABFacet<TValue>> GetFacetAsync<TValue>(
        FacetQuery<TValue> query,
        CancellationToken cancellationToken = default)
        where TValue : class
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        string requestUri = ABQueryStringBuilder.Build(query, _options).RequestUri;
        _facetRequests.Add(new FacetRequest(query.FacetName, query.Filter));
        _requestUris.Add(requestUri);

        if (FacetResponses.TryGetValue(query.FacetName, out object? configured))
        {
            return Task.FromResult((ABFacet<TValue>)configured);
        }

        return Task.FromResult(new ABFacet<TValue>
        {
            FacetName = query.FacetName,
            ReportedCount = 0,
            Values = [],
        });
    }

    /// <inheritdoc/>
    public Task<ABFacet<Region>> GetRegionFacetAsync(
        StandardsFilter? filter = null,
        CancellationToken cancellationToken = default)
        => GetFacetAsync(
            new FacetQuery<Region> { FacetName = ABFacetNames.Regions, Filter = filter },
            cancellationToken);

    /// <inheritdoc/>
    public Task<ABFacet<Authority>> GetAuthorityFacetAsync(
        StandardsFilter? filter = null,
        CancellationToken cancellationToken = default)
        => GetFacetAsync(
            new FacetQuery<Authority> { FacetName = ABFacetNames.Authorities, Filter = filter },
            cancellationToken);

    /// <inheritdoc/>
    public Task<ABFacet<Publication>> GetPublicationFacetAsync(
        StandardsFilter? filter = null,
        CancellationToken cancellationToken = default)
        => GetFacetAsync(
            new FacetQuery<Publication> { FacetName = ABFacetNames.Publications, Filter = filter },
            cancellationToken);

    /// <inheritdoc/>
    public Task<ABFacet<DocumentSummary>> GetDocumentFacetAsync(
        StandardsFilter? filter = null,
        CancellationToken cancellationToken = default)
        => GetFacetAsync(
            new FacetQuery<DocumentSummary> { FacetName = ABFacetNames.Documents, Filter = filter },
            cancellationToken);

    /// <inheritdoc/>
    public Task<ABFacet<SectionSummary>> GetSectionFacetAsync(
        StandardsFilter? filter = null,
        CancellationToken cancellationToken = default)
        => GetFacetAsync(
            new FacetQuery<SectionSummary> { FacetName = ABFacetNames.Sections, Filter = filter },
            cancellationToken);

    /// <summary>One recorded facet call.</summary>
    /// <param name="FacetName">The facet name the caller asked for.</param>
    /// <param name="Filter">The scope the facet was computed over, or null for none.</param>
    public sealed record FacetRequest(string FacetName, StandardsFilter? Filter);

    /// <summary>
    /// A throttle state that reports a full bucket and no activity. The feed never touches the
    /// throttle, so there is nothing here to count.
    /// </summary>
    private sealed class InertThrottleState : IABConnectThrottleState
    {
        public int AvailableTokens => 25;

        public int BucketCapacity => 25;

        public long AcquiredCount => 0;

        public long WaitCount => 0;

        public TimeSpan TotalWaitTime => TimeSpan.Zero;

        public long ThrottleResponseCount => 0;
    }
}
