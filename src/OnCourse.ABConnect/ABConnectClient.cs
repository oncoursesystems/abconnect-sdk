using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OnCourse.ABConnect.Http;
using OnCourse.ABConnect.Models;
using OnCourse.ABConnect.Queries;
using OnCourse.ABConnect.Throttling;

namespace OnCourse.ABConnect;

/// <summary>
/// The default <see cref="IABConnectClient"/>: one HTTP request per method, no paging, no retry
/// beyond the transport pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The value-or-throw guarantee holds on every method: the method either returns a non-null value that was
/// deserialized from a 2xx response body, or it throws. It never returns null, and it never returns
/// a default-constructed envelope. An <c>ABPage&lt;T&gt;</c> with <c>Data.Count == 0</c> therefore
/// means, unambiguously, that AB Connect answered successfully and matched nothing.
/// </para>
/// <para>
/// The implementation makes that structural rather than aspirational. There is exactly one send path,
/// it keeps the <see cref="HttpResponseMessage"/> instead of asking for a string, and every
/// <c>catch</c> in this type either rethrows the caught exception unchanged or throws an
/// <see cref="ABConnectException"/> built from it. No <c>catch</c> in this type produces a return
/// value, so there is no code path that can turn a failure into an empty page.
/// </para>
/// <para>
/// Paging, ordering, and completeness are deliberately absent. They live on
/// <see cref="IABConnectFeed"/>, which composes this client. A facet is fetched with exactly one
/// request and is never paged, because AB Connect does not support paging of facet data.
/// </para>
/// </remarks>
public sealed class ABConnectClient : IABConnectClient
{
    /// <summary>
    /// Logged before a request leaves the client, at debug level. The path is
    /// <see cref="ABConnectRequestContext.RedactedPath"/>, so no credential can reach a log sink.
    /// </summary>
    private static readonly Action<ILogger, string, bool, Exception?> LogSendingRequest =
        LoggerMessage.Define<string, bool>(
            LogLevel.Debug,
            new EventId(2001, "ABConnectRequestSending"),
            "Sending AB Connect request to {RequestPath} (wildcard: {IsWildcardRequest}).");

    /// <summary>
    /// Logged once a response has been received, at debug level, before it is mapped. The body is
    /// never logged.
    /// </summary>
    private static readonly Action<ILogger, int, string, int, Exception?> LogReceivedResponse =
        LoggerMessage.Define<int, string, int>(
            LogLevel.Debug,
            new EventId(2002, "ABConnectResponseReceived"),
            "AB Connect returned HTTP {StatusCode} for {RequestPath} after {Attempts} attempt(s).");

    private readonly HttpClient _httpClient;
    private readonly ABConnectOptions _options;
    private readonly IABConnectRateLimiterProvider _rateLimiterProvider;
    private readonly ILogger<ABConnectClient> _logger;

    /// <summary>Creates the client.</summary>
    /// <remarks>
    /// There is no constructor that can bypass options validation. Constructing a client without a
    /// partner id or partner key fails here rather than producing requests signed with an empty key.
    /// </remarks>
    /// <param name="httpClient">The typed client, already carrying the base address, timeout, and handler pipeline.</param>
    /// <param name="options">The validated SDK options.</param>
    /// <param name="rateLimiterProvider">The shared token buckets, surfaced through <see cref="Throttle"/>.</param>
    /// <param name="logger">The logger for request diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ABConnectConfigurationException">The partner id or partner key is null or empty.</exception>
    public ABConnectClient(
        HttpClient httpClient,
        IOptions<ABConnectOptions> options,
        IABConnectRateLimiterProvider rateLimiterProvider,
        ILogger<ABConnectClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rateLimiterProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _options = options.Value;
        _rateLimiterProvider = rateLimiterProvider;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.PartnerId) || string.IsNullOrWhiteSpace(_options.PartnerKey))
        {
            throw new ABConnectConfigurationException(
                $"AB Connect requires both {nameof(ABConnectOptions.PartnerId)} and " +
                $"{nameof(ABConnectOptions.PartnerKey)} to be configured in the " +
                $"'{ABConnectOptions.SectionName}' section.");
        }
    }

    /// <inheritdoc />
    public IABConnectThrottleState Throttle => _rateLimiterProvider;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Exactly one HTTP request, whose response is returned as it came back. No paging happens here:
    /// the page window is whatever <see cref="StandardsQuery.Page"/> asked for, lowered to
    /// <see cref="ABConnectOptions.PageSize"/> by the query builder.
    /// </para>
    /// <para>
    /// This method either returns a non-null value that was deserialized from a 2xx
    /// response body, or it throws. It never returns null, and it never returns a default-constructed
    /// envelope. An <c>ABPage&lt;T&gt;</c> with <c>Data.Count == 0</c> therefore means, unambiguously,
    /// that AB Connect answered successfully and matched nothing.
    /// </para>
    /// </remarks>
    public async Task<ABPage<Standard>> GetStandardsAsync(
        StandardsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        (string requestUri, ABConnectRequestContext context) = ABQueryStringBuilder.Build(query, _options);

        bool dataRequired = query.Page.Limit > 0;

        return await SendAsync<ABPageDocument<Standard>, ABPage<Standard>>(
            requestUri,
            context,
            document => document.ToPageOrNull(dataRequired),
            "a page of standards",
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// AB Connect answers this route for a deleted standard as well as an active one, which makes it
    /// the documented remedy for a stale local copy and the only way to resolve a retired GUID. A
    /// deleted standard is a successful answer, not a failure: it arrives with
    /// <c>status</c> of <see cref="ABStandardStatuses.Deleted"/> and a populated
    /// <see cref="StandardAttributes.DateDeletedUtc"/>.
    /// </para>
    /// <para>
    /// HTTP 404 becomes <see cref="ABConnectNotFoundException"/> and HTTP 403 becomes
    /// <see cref="ABConnectNotLicensedException"/>, so an unknown GUID and a GUID outside the
    /// account's license are distinguishable without inspecting a status code.
    /// </para>
    /// <para>
    /// This method either returns a non-null value that was deserialized from a 2xx
    /// response body, or it throws. It never returns null, and it never returns a default-constructed
    /// envelope.
    /// </para>
    /// </remarks>
    public async Task<Standard> GetStandardAsync(
        string guid,
        StandardFieldSet? fields = null,
        CancellationToken cancellationToken = default)
    {
        (string requestUri, ABConnectRequestContext context) =
            ABQueryStringBuilder.BuildStandardLookup(guid, fields, _options);

        return await SendAsync<ABResourceDocument, Standard>(
            requestUri,
            context,
            static document => document.Unwrap<Standard>(ABConnectJson.Default),
            "a single standard",
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Exactly one HTTP request. Walking the event stream to its end, and re-anchoring on the highest
    /// sequence seen rather than on an offset, is <see cref="IABConnectFeed.ReadEventsAsync"/>'s job,
    /// not this method's.
    /// </para>
    /// <para>
    /// This method either returns a non-null value that was deserialized from a 2xx
    /// response body, or it throws. It never returns null, and it never returns a default-constructed
    /// envelope. An <c>ABPage&lt;T&gt;</c> with <c>Data.Count == 0</c> therefore means, unambiguously,
    /// that AB Connect answered successfully and matched nothing, which on this endpoint means the
    /// caller's watermark is current.
    /// </para>
    /// </remarks>
    public async Task<ABPage<ABEvent>> GetEventsAsync(
        EventsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        (string requestUri, ABConnectRequestContext context) = ABQueryStringBuilder.Build(query, _options);

        bool dataRequired = query.Page.Limit > 0;

        return await SendAsync<ABPageDocument<ABEvent>, ABPage<ABEvent>>(
            requestUri,
            context,
            document => document.ToPageOrNull(dataRequired),
            "a page of events",
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Exactly one HTTP request, carrying <c>limit=0</c> and <c>facet_summary</c>, and never a second
    /// one. AB Connect does not support paging of facet data and returns at most the first 10,000
    /// values of a facet, so the result reports both AB Connect's own count and the values that
    /// actually arrived; compare them through <see cref="ABFacet{TValue}.IsTruncated"/> before
    /// treating the values as the whole facet.
    /// </para>
    /// <para>
    /// This method either returns a non-null value that was deserialized from a 2xx
    /// response body, or it throws. It never returns null, and it never returns a default-constructed
    /// envelope. A facet with no values means AB Connect answered successfully and the facet has no
    /// values over the filtered standards.
    /// </para>
    /// </remarks>
    public async Task<ABFacet<TValue>> GetFacetAsync<TValue>(
        FacetQuery<TValue> query,
        CancellationToken cancellationToken = default)
        where TValue : class
    {
        ArgumentNullException.ThrowIfNull(query);

        (string requestUri, ABConnectRequestContext context) = ABQueryStringBuilder.Build(query, _options);
        string facetName = query.FacetName;

        return await SendAsync<ABFacetEnvelope, ABFacet<TValue>>(
            requestUri,
            context,
            envelope => envelope.ToFacet<TValue>(facetName, ABConnectJson.Default),
            $"the facet summary '{facetName}'",
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A wrapper over <see cref="GetFacetAsync{TValue}"/> for <see cref="ABFacetNames.Regions"/>. The
    /// facet name lives in the SDK, never at a call site, so that a typo cannot silently produce an
    /// empty result. This method either returns a non-null value that was deserialized
    /// from a 2xx response body, or it throws. It never returns null, and it never returns a
    /// default-constructed envelope.
    /// </remarks>
    public Task<ABFacet<Region>> GetRegionFacetAsync(
        StandardsFilter? filter = null,
        CancellationToken cancellationToken = default)
        => GetFacetAsync(
            new FacetQuery<Region> { FacetName = ABFacetNames.Regions, Filter = filter },
            cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// A wrapper over <see cref="GetFacetAsync{TValue}"/> for
    /// <see cref="ABFacetNames.Authorities"/>. The facet name lives in the SDK, never at a call site,
    /// so that a typo cannot silently produce an empty result. This method either returns
    /// a non-null value that was deserialized from a 2xx response body, or it throws. It never returns
    /// null, and it never returns a default-constructed envelope.
    /// </remarks>
    public Task<ABFacet<Authority>> GetAuthorityFacetAsync(
        StandardsFilter? filter = null,
        CancellationToken cancellationToken = default)
        => GetFacetAsync(
            new FacetQuery<Authority> { FacetName = ABFacetNames.Authorities, Filter = filter },
            cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// A wrapper over <see cref="GetFacetAsync{TValue}"/> for
    /// <see cref="ABFacetNames.Publications"/>. The values project into <see cref="Publication"/>, the
    /// thin facet shape, not <see cref="StandardPublication"/>. This method either returns
    /// a non-null value that was deserialized from a 2xx response body, or it throws. It never returns
    /// null, and it never returns a default-constructed envelope.
    /// </remarks>
    public Task<ABFacet<Publication>> GetPublicationFacetAsync(
        StandardsFilter? filter = null,
        CancellationToken cancellationToken = default)
        => GetFacetAsync(
            new FacetQuery<Publication> { FacetName = ABFacetNames.Publications, Filter = filter },
            cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// A wrapper over <see cref="GetFacetAsync{TValue}"/> for <see cref="ABFacetNames.Documents"/>.
    /// The values project into <see cref="DocumentSummary"/>, which carries a GUID, a description, and
    /// an adopt year only; read a standard with <see cref="StandardFieldSet.Probe"/> to get the rest of
    /// a document. This method either returns a non-null value that was deserialized from
    /// a 2xx response body, or it throws. It never returns null, and it never returns a
    /// default-constructed envelope.
    /// </remarks>
    public Task<ABFacet<DocumentSummary>> GetDocumentFacetAsync(
        StandardsFilter? filter = null,
        CancellationToken cancellationToken = default)
        => GetFacetAsync(
            new FacetQuery<DocumentSummary> { FacetName = ABFacetNames.Documents, Filter = filter },
            cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// A wrapper over <see cref="GetFacetAsync{TValue}"/> for <see cref="ABFacetNames.Sections"/>.
    /// This is the facet most likely to exceed AB Connect's 10,000-value ceiling, so check
    /// <see cref="ABFacet{TValue}.IsTruncated"/> on the result. This method either returns
    /// a non-null value that was deserialized from a 2xx response body, or it throws. It never returns
    /// null, and it never returns a default-constructed envelope.
    /// </remarks>
    public Task<ABFacet<SectionSummary>> GetSectionFacetAsync(
        StandardsFilter? filter = null,
        CancellationToken cancellationToken = default)
        => GetFacetAsync(
            new FacetQuery<SectionSummary> { FacetName = ABFacetNames.Sections, Filter = filter },
            cancellationToken);

    /// <summary>
    /// The single send path: issue one GET, map a non-2xx response to an exception, deserialize a 2xx
    /// body into its wire document, and project that document onto the public shape.
    /// </summary>
    /// <typeparam name="TDocument">The internal wire shape the response body deserializes into.</typeparam>
    /// <typeparam name="TResult">The public shape the wire document projects onto.</typeparam>
    /// <param name="requestUri">The credential-free relative URI the query builder produced.</param>
    /// <param name="context">
    /// The request context the query builder produced. It is attached to the request message so that
    /// the throttle handler can read the wildcard flag, the retry handler can count attempts, and
    /// every exception can report the path as it was before credentials were appended.
    /// </param>
    /// <param name="project">
    /// Projects the wire document onto the public shape, returning null when the body was a
    /// well-formed JSON document that did not actually carry the expected payload.
    /// </param>
    /// <param name="expectation">What the body was expected to be, for the format-failure message.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The projected result. Never null.</returns>
    /// <remarks>
    /// <para>
    /// The value-or-throw guarantee is enforced here, and it is the reason this method has the shape it has: the
    /// method either returns a non-null value that was deserialized from a 2xx response body, or it
    /// throws. It never returns null, and it never returns a default-constructed envelope. Every
    /// <c>catch</c> below rethrows or throws; none of them yields a value. A null projection is a
    /// format failure, not an empty result, because "AB Connect matched nothing" and "AB Connect did
    /// not answer with a list" must never be confused.
    /// </para>
    /// <para>
    /// The response message is kept rather than discarded, which is what makes the status code and the
    /// <c>Retry-After</c> header available to the mapper. The default completion option is used on
    /// purpose: the body is buffered inside the handler pipeline, so no content read outlives the
    /// per-attempt cancellation source the retry handler owns.
    /// </para>
    /// <para>
    /// A cancellation the caller requested is rethrown untouched, so it surfaces as a bare
    /// <see cref="OperationCanceledException"/> per .NET convention. A transport failure or a
    /// per-attempt timeout that survived the retry ladder is wrapped as
    /// <see cref="ABConnectTransportException"/> with the original failure as its inner exception.
    /// </para>
    /// </remarks>
    private async Task<TResult> SendAsync<TDocument, TResult>(
        string requestUri,
        ABConnectRequestContext context,
        Func<TDocument, TResult?> project,
        string expectation,
        CancellationToken cancellationToken)
        where TResult : class
    {
        LogSendingRequest(_logger, context.RedactedPath, context.IsWildcardRequest, null);

        using HttpRequestMessage request = new(HttpMethod.Get, requestUri);
        context.AttachTo(request);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception cause) when (cause is HttpRequestException or TaskCanceledException or IOException)
        {
            throw ABConnectResponseMapper.CreateTransportException(cause, context);
        }

        using (response)
        {
            LogReceivedResponse(
                _logger,
                (int)response.StatusCode,
                context.RedactedPath,
                AttemptsOf(context),
                null);

            TDocument document = await ABConnectResponseMapper
                .ReadAsync<TDocument>(response, context, ABConnectJson.Default, cancellationToken)
                .ConfigureAwait(false);

            TResult? result;
            try
            {
                result = project(document);
            }
            catch (JsonException cause)
            {
                throw FormatFailure(context, response, expectation, cause.Message, cause);
            }

            return result ?? throw FormatFailure(
                context,
                response,
                expectation,
                "the body carried no such payload",
                cause: null);
        }
    }

    /// <summary>
    /// Builds the format failure raised when a 2xx body parsed as JSON but did not carry the payload
    /// the endpoint promises. It is a failure and not an empty result, because the value-or-throw guarantee reserves an
    /// empty result for the case where AB Connect answered successfully and matched nothing.
    /// </summary>
    private static ABConnectResponseFormatException FormatFailure(
        ABConnectRequestContext context,
        HttpResponseMessage response,
        string expectation,
        string reason,
        Exception? cause)
    {
        string message = string.Format(
            CultureInfo.InvariantCulture,
            "AB Connect returned HTTP {0} for {1} but the body could not be read as {2}: {3}",
            (int)response.StatusCode,
            context.RedactedPath,
            expectation,
            reason);

        return new ABConnectResponseFormatException(
            message,
            context.RedactedPath,
            response.StatusCode,
            [],
            AttemptsOf(context),
            cause);
    }

    /// <summary>
    /// The attempt count to report. The retry handler records one attempt per send; a context that
    /// never reached that handler still describes a single attempt, never zero.
    /// </summary>
    private static int AttemptsOf(ABConnectRequestContext context)
        => context.Attempts > 0 ? context.Attempts : 1;
}
