using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OnCourse.ABConnect.Feed;
using OnCourse.ABConnect.Models;
using OnCourse.ABConnect.Queries;

namespace OnCourse.ABConnect;

/// <summary>
/// The default <see cref="IABConnectFeed"/>: paging, ordering, and completeness over
/// <see cref="IABConnectClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// Holds no HTTP of its own, which is what makes the interesting logic testable against a fake
/// client. The value-or-throw guarantee holds on every method: the method either returns a non-null value assembled
/// from 2xx response bodies, or it throws.
/// </para>
/// <para>
/// There is no <c>catch</c> block anywhere in this type. A failure on the fourth page of a traversal
/// propagates out of the fourth iteration, and the pages already produced were real.
/// </para>
/// <para>
/// The effective rows-per-page for every traversal is the smaller of the read option and
/// <see cref="ABConnectOptions.PageSize"/>, because the query builder lowers any requested limit to
/// the configured page size. Where AB Connect echoes the limit it actually applied in
/// <see cref="PageMeta.Limit"/>, that echo wins, so the offset arithmetic and the request bound are
/// computed from what the service did rather than from what was asked for.
/// </para>
/// </remarks>
public sealed class ABConnectFeed : IABConnectFeed
{
    /// <summary>
    /// The redacted path reported by the probes when a matched row does not carry the object the
    /// probe requested. The probes are the only place this type raises a request-shaped failure, and
    /// they build their query through <see cref="IABConnectClient"/>, so no signed URI is available
    /// to name here.
    /// </summary>
    private const string ProbeRequestPath = "standards";

    private const int EventTraversalCompletedEventIdValue = 4001;
    private const int DocumentSnapshotCompletedEventIdValue = 4002;

    /// <summary>
    /// Logs one completed event traversal. Nothing is logged per page and nothing whatsoever is
    /// logged about sequence gaps, which are normal.
    /// </summary>
    private static readonly Action<ILogger, long, int, int, bool, Exception?> LogEventTraversalCompleted =
        LoggerMessage.Define<long, int, int, bool>(
            LogLevel.Debug,
            new EventId(EventTraversalCompletedEventIdValue, "ABConnectEventTraversalCompleted"),
            "AB Connect event traversal past sequence {AfterSequence} read {EventCount} events in " +
            "{RequestCount} requests. Complete: {IsComplete}");

    /// <summary>Logs one completed and verified document snapshot.</summary>
    private static readonly Action<ILogger, string, int, int, Exception?> LogDocumentSnapshotCompleted =
        LoggerMessage.Define<string, int, int>(
            LogLevel.Debug,
            new EventId(DocumentSnapshotCompletedEventIdValue, "ABConnectDocumentSnapshotCompleted"),
            "AB Connect document snapshot for {DocumentGuid} read {StandardCount} standards in " +
            "{RequestCount} requests.");

    private readonly IABConnectClient _client;
    private readonly ABConnectOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ABConnectFeed> _logger;

    /// <summary>Creates the feed.</summary>
    /// <param name="client">The low-level client every page is fetched through.</param>
    /// <param name="options">The validated SDK options.</param>
    /// <param name="timeProvider">The clock that stamps <see cref="DocumentSnapshot.FetchedAtUtc"/>.</param>
    /// <param name="logger">The logger for traversal diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ABConnectFeed(
        IABConnectClient client,
        IOptions<ABConnectOptions> options,
        TimeProvider timeProvider,
        ILogger<ABConnectFeed> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Reads all events with a sequence strictly greater than the watermark, in ascending sequence
    /// order, buffered into a single result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value-or-throw guarantee, this package's headline promise. For every method on <see cref="IABConnectClient"/> and
    /// <see cref="IABConnectFeed"/>: the method either returns a non-null value that was deserialized
    /// from a 2xx response body, or it throws. It never returns null, and it never returns a
    /// default-constructed envelope. An <see cref="ABPage{T}"/> with <c>Data.Count == 0</c> therefore
    /// means, unambiguously, that AB Connect answered successfully and matched nothing.
    /// </para>
    /// <para>
    /// The traversal never uses <c>offset</c>. Every request is issued at offset zero with
    /// <c>filter[events]=(seq GT n)</c>, where <c>n</c> is the highest sequence seen so far, and
    /// <see cref="PageLinks.Next"/> is never inspected. Re-anchoring on the sequence is what makes
    /// the walk resumable: a crash mid-traversal leaves the caller holding a valid watermark.
    /// </para>
    /// <para>
    /// Sequence numbers are not contiguous and a gap is never treated as a problem. AB Connect
    /// assigns every event in the system a unique number, including partner-specific events such as
    /// license and delivery changes that this account cannot see, so the numbers this account
    /// receives necessarily skip. This SDK performs no gap detection: a gap does not throw, does not
    /// warn, and is not logged at any level.
    /// </para>
    /// <para>
    /// The traversal stops when a request returns no events, so a completed read costs one request
    /// more than the number of pages it returned. Termination does not depend on
    /// <see cref="PageMeta.Count"/> or on a short page, because a count that understates the feed or
    /// a page shortened by server-side filtering would otherwise silently truncate a delta and there
    /// is no way to recover events a watermark has already moved past. Guard 2 below is what proves
    /// the loop terminates: every page carrying events raises the watermark strictly.
    /// </para>
    /// </remarks>
    /// <param name="afterSequence">The watermark. Only events past this sequence are returned.</param>
    /// <param name="options">Read options, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the traversal.</param>
    /// <returns>
    /// The batch. Never null; an empty <c>Events</c> list means the service answered and there is
    /// nothing past the watermark, per the value-or-throw guarantee. <c>HighestSequence</c> is null if and only if
    /// <c>Events</c> is empty. <c>IsComplete</c> is false only when
    /// <see cref="EventReadOptions.MaxEvents"/> stopped the read, including the boundary case where
    /// the ceiling happened to coincide with the end of the feed; a truncated batch is a legitimate
    /// operational choice rather than a failure, and the caller resumes from <c>HighestSequence</c>.
    /// </returns>
    /// <exception cref="ABConnectRequestException">Any page failed.</exception>
    /// <exception cref="ABConnectPagingException">
    /// The service returned events out of ascending sequence order, returned a page that cannot
    /// advance the watermark, returned an event at or below the watermark it was given, or returned
    /// an event carrying no attributes and therefore no sequence.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="EventReadOptions.PageSize"/> is less than one, or
    /// <see cref="EventReadOptions.MaxEvents"/> is set and less than one.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<EventBatch> ReadEventsAsync(
        long afterSequence,
        EventReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EventWalkPlan plan = CreateEventWalkPlan(options);
        EventWalkState state = new();
        List<ABEvent> events = [];

        await foreach (EventPage page in WalkEventsAsync(afterSequence, plan, state, cancellationToken)
            .ConfigureAwait(false))
        {
            events.AddRange(page.Events);
        }

        LogEventTraversalCompleted(
            _logger,
            afterSequence,
            events.Count,
            state.RequestsIssued,
            state.IsComplete,
            null);

        return new EventBatch
        {
            Events = events.AsReadOnly(),
            AfterSequence = afterSequence,
            HighestSequence = state.HighestSequence,
            ReportedTotalCount = state.ReportedTotalCount,
            PagesFetched = state.RequestsIssued,
            IsComplete = state.IsComplete,
        };
    }

    /// <summary>
    /// The same traversal as <see cref="ReadEventsAsync"/>, surfaced one page at a time so a caller
    /// can persist and advance its watermark incrementally.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value-or-throw guarantee applies to every page: a yielded page was deserialized from a 2xx response body,
    /// and a failure throws from the iteration that hit it rather than ending the enumeration
    /// quietly.
    /// </para>
    /// <para>
    /// Sequence gaps are normal and are never treated as a problem: no gap detection is performed, so
    /// a gap does not throw, does not warn, and is not logged at any level.
    /// </para>
    /// <para>
    /// Each page's <see cref="EventPage.HighestSequence"/> is the watermark to persist once that page
    /// has been applied. A caller that stops enumerating early holds a valid watermark and can resume
    /// from it; the events it did not see are still past that watermark.
    /// </para>
    /// <para>
    /// <see cref="EventReadOptions.MaxEvents"/> caps the total number of events yielded across the
    /// whole enumeration, truncating the final page if necessary, and simply ends the enumeration
    /// when it bites. A page stream cannot report <c>IsComplete</c>, so a caller that needs to know
    /// whether a ceiling stopped the read should use <see cref="ReadEventsAsync"/> instead.
    /// </para>
    /// </remarks>
    /// <param name="afterSequence">The watermark. Only events past this sequence are returned.</param>
    /// <param name="options">Read options, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the traversal.</param>
    /// <returns>
    /// The pages, in order. Yields zero pages when there is nothing past the watermark, and never
    /// yields an empty page, so <see cref="EventPage.HighestSequence"/> is always a real sequence
    /// taken from a real event.
    /// </returns>
    /// <exception cref="ABConnectRequestException">Any page failed. Thrown from the iteration that fetched it.</exception>
    /// <exception cref="ABConnectPagingException">
    /// The service returned events out of ascending sequence order, returned a page that cannot
    /// advance the watermark, returned an event at or below the watermark it was given, or returned
    /// an event carrying no attributes and therefore no sequence.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="EventReadOptions.PageSize"/> is less than one, or
    /// <see cref="EventReadOptions.MaxEvents"/> is set and less than one. Raised by this call rather
    /// than deferred to the first iteration.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public IAsyncEnumerable<EventPage> ReadEventPagesAsync(
        long afterSequence,
        EventReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EventWalkPlan plan = CreateEventWalkPlan(options);
        return WalkEventsAsync(afterSequence, plan, new EventWalkState(), cancellationToken);
    }

    /// <summary>
    /// Reads a complete, verified snapshot of every standard in one document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value-or-throw guarantee, this package's headline promise. For every method on <see cref="IABConnectClient"/> and
    /// <see cref="IABConnectFeed"/>: the method either returns a non-null value that was deserialized
    /// from a 2xx response body, or it throws. It never returns null, and it never returns a
    /// default-constructed envelope. A snapshot whose <c>Standards</c> list is empty therefore means,
    /// unambiguously, that AB Connect answered successfully and the document has no standards in the
    /// requested status scope.
    /// </para>
    /// <para>
    /// The traversal pages by offset, because there is no cursor for standards, and requests
    /// <c>sort[standards]=seq,guid</c> every time so that the ordering is total and offset paging is
    /// therefore stable. Before returning, all three completeness checks must pass, and any failure
    /// throws <see cref="ABConnectPagingException"/>: the number of distinct standard GUIDs equals
    /// the number of rows collected, the number of distinct GUIDs equals the count AB Connect
    /// reported on the first page with a tolerance of zero, and the walk both stops at the first
    /// short page and refuses to issue more than <c>ceil(meta.count / limit) + 2</c> requests.
    /// </para>
    /// <para>
    /// The shortfall check fires when a document is edited between the first page and the last: rows
    /// shift under the walk and the gathered set no longer reconciles with the reported count.
    /// Failing is the correct outcome. The snapshot is not internally consistent, it must not be
    /// diffed against a local mirror, and the caller should simply refetch the document. This is a
    /// deliberate behavior change from version 2, which silently wrote an inconsistent snapshot.
    /// </para>
    /// <para>
    /// A row's identity is <c>attributes.guid</c> when the requested field set carries it, and the
    /// JSON:API resource id otherwise, so narrowing the field set does not weaken the duplicate
    /// check. Identities compare case-insensitively, because AB Connect's GUID casing is not
    /// guaranteed to be stable across pages.
    /// </para>
    /// </remarks>
    /// <param name="documentGuid">The AB Connect GUID of the document.</param>
    /// <param name="options">Read options, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the traversal.</param>
    /// <returns>The snapshot. Never null, per the value-or-throw guarantee.</returns>
    /// <exception cref="ABConnectRequestException">Any page failed.</exception>
    /// <exception cref="ABConnectPagingException">
    /// A standard was returned twice, the gathered rows did not reconcile with the reported count, or
    /// the walk would have needed more requests than the bound allows.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="documentGuid"/> is empty or is not a well-formed GUID.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="DocumentReadOptions.PageSize"/> is less than one.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<DocumentSnapshot> ReadDocumentSnapshotAsync(
        string documentGuid,
        DocumentReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string guid = StandardsFilter.ValidateGuid(documentGuid, nameof(documentGuid));
        DocumentWalkPlan plan = CreateDocumentWalkPlan(options);
        DocumentWalkState state = new();
        DateTimeOffset startedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        List<Standard> standards = [];

        await foreach (StandardPage page in WalkDocumentAsync(guid, plan, state, cancellationToken)
            .ConfigureAwait(false))
        {
            standards.AddRange(page.Standards);
        }

        VerifySnapshotCompleteness(guid, standards.Count, state);

        LogDocumentSnapshotCompleted(_logger, guid, standards.Count, state.RequestsIssued, null);

        return new DocumentSnapshot
        {
            DocumentGuid = guid,
            Standards = standards.AsReadOnly(),
            ReportedTotalCount = state.ReportedTotalCount,
            PagesFetched = state.RequestsIssued,
            FetchedAtUtc = startedAtUtc,
        };
    }

    /// <summary>
    /// The same traversal as <see cref="ReadDocumentSnapshotAsync"/>, streamed by page, for callers
    /// that will not hold a document in memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value-or-throw guarantee applies to every page: a yielded page was deserialized from a 2xx response body,
    /// and a failure throws from the iteration that hit it rather than ending the enumeration
    /// quietly.
    /// </para>
    /// <para>
    /// Streaming gives up the completeness guarantee. Duplicate detection and the request bound still
    /// apply, but the shortfall check cannot: it can only be evaluated once the whole document has
    /// been read, and this enumeration exists precisely so that a caller need not do that. A caller
    /// that stops enumerating early holds an arbitrary prefix of the document, not a snapshot, and
    /// must not diff it against a local mirror for deletions.
    /// </para>
    /// </remarks>
    /// <param name="documentGuid">The AB Connect GUID of the document.</param>
    /// <param name="options">Read options, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the traversal.</param>
    /// <returns>The pages, in order. Yields zero pages for a document with no matching standards, and never yields an empty page.</returns>
    /// <exception cref="ABConnectRequestException">Any page failed. Thrown from the iteration that fetched it.</exception>
    /// <exception cref="ABConnectPagingException">
    /// A standard was returned twice, or the walk would have needed more requests than the bound
    /// allows.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="documentGuid"/> is empty or is not a well-formed GUID. Raised by this call
    /// rather than deferred to the first iteration.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="DocumentReadOptions.PageSize"/> is less than one.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public IAsyncEnumerable<StandardPage> ReadDocumentPagesAsync(
        string documentGuid,
        DocumentReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string guid = StandardsFilter.ValidateGuid(documentGuid, nameof(documentGuid));
        DocumentWalkPlan plan = CreateDocumentWalkPlan(options);
        return WalkDocumentAsync(guid, plan, new DocumentWalkState(), cancellationToken);
    }

    /// <summary>
    /// Probes for one fully populated document with a single one-row request.
    /// </summary>
    /// <remarks>
    /// Asks for <see cref="StandardFieldSet.Probe"/> at <c>limit=1</c> and returns the document
    /// embedded in the matched standard. This replaces the wildcard-limit-1 call it supersedes, which
    /// paid the wildcard throttle penalty of two requests per second for the same two fields. Deleted
    /// standards are in scope, because a document all of whose standards have been deleted still
    /// exists and a caller reconciling a mirror still needs to describe it.
    /// </remarks>
    /// <param name="documentGuid">The AB Connect GUID of the document.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The document, or <see langword="null"/> when AB Connect answered successfully and no standard
    /// in the account's license belongs to that document. Null here means "answered, and there is
    /// none", never "something failed"; a failure throws.
    /// </returns>
    /// <exception cref="ABConnectRequestException">The request failed or its body could not be read.</exception>
    /// <exception cref="ABConnectResponseFormatException">
    /// A standard matched but carried no embedded document, so the answer can be neither returned nor
    /// honestly reported as "there is none".
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="documentGuid"/> is empty or is not a well-formed GUID.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<StandardDocument?> ReadDocumentAsync(
        string documentGuid,
        CancellationToken cancellationToken = default)
    {
        string guid = StandardsFilter.ValidateGuid(documentGuid, nameof(documentGuid));

        Standard? row = await ProbeAsync(StandardsFilter.ByDocument(guid), cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        return row.Attributes?.Document
            ?? throw new ABConnectResponseFormatException(
                $"AB Connect matched a standard for document '{guid}' but the row carried no embedded " +
                "document, even though the probe requested the document field.",
                ProbeRequestPath,
                null,
                null,
                1);
    }

    /// <summary>
    /// Probes for one fully populated publication with a single one-row request.
    /// </summary>
    /// <remarks>
    /// Asks for <see cref="StandardFieldSet.Probe"/> at <c>limit=1</c> and returns the publication
    /// embedded in the matched standard's document. This replaces the wildcard-limit-1 call it
    /// supersedes, which paid the wildcard throttle penalty of two requests per second for the same
    /// two fields. Deleted standards are in scope, because a publication whose standards have all
    /// been deleted still exists.
    /// </remarks>
    /// <param name="publicationGuid">The AB Connect GUID of the publication.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The publication, or <see langword="null"/> when AB Connect answered successfully and no
    /// standard in the account's license belongs to that publication. Null here means "answered, and
    /// there is none", never "something failed"; a failure throws.
    /// </returns>
    /// <exception cref="ABConnectRequestException">The request failed or its body could not be read.</exception>
    /// <exception cref="ABConnectResponseFormatException">
    /// A standard matched but carried no embedded publication, so the answer can be neither returned
    /// nor honestly reported as "there is none".
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="publicationGuid"/> is empty or is not a well-formed GUID.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<StandardPublication?> ReadPublicationAsync(
        string publicationGuid,
        CancellationToken cancellationToken = default)
    {
        string guid = StandardsFilter.ValidateGuid(publicationGuid, nameof(publicationGuid));

        Standard? row = await ProbeAsync(StandardsFilter.ByPublication(guid), cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        return row.Attributes?.Document?.Publication
            ?? throw new ABConnectResponseFormatException(
                $"AB Connect matched a standard for publication '{guid}' but the row carried no " +
                "embedded publication, even though the probe requested the document.publication field.",
                ProbeRequestPath,
                null,
                null,
                1);
    }

    /// <summary>
    /// The shared events traversal. Both public event methods run this walk; the buffering one
    /// simply concatenates the pages and reads the shared <paramref name="state"/> afterwards.
    /// </summary>
    /// <param name="afterSequence">The starting watermark.</param>
    /// <param name="plan">The validated read plan.</param>
    /// <param name="state">Accumulates what the caller needs after the walk. Mutated in place.</param>
    /// <param name="cancellationToken">Cancels the traversal.</param>
    /// <returns>The non-empty pages of the traversal, in order.</returns>
    private async IAsyncEnumerable<EventPage> WalkEventsAsync(
        long afterSequence,
        EventWalkPlan plan,
        EventWalkState state,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long watermark = afterSequence;
        int pageNumber = 0;

        while (true)
        {
            int limit = plan.PageSize;

            if (plan.MaxEvents is int ceiling)
            {
                int remaining = ceiling - state.EventsRead;
                if (remaining <= 0)
                {
                    state.IsComplete = false;
                    yield break;
                }

                limit = Math.Min(limit, remaining);
            }

            EventsQuery query = new()
            {
                AfterSequence = watermark,
                Fields = EventFieldSet.Full,
                Page = new PageRequest(0, limit),
                StandardScope = plan.StandardScope,
            };

            ABPage<ABEvent> page = await _client.GetEventsAsync(query, cancellationToken)
                .ConfigureAwait(false);
            state.RequestsIssued++;

            if (state.RequestsIssued == 1)
            {
                state.ReportedTotalCount = page.Meta.Count;
            }

            if (page.Data.Count == 0)
            {
                yield break;
            }

            long highestSequence = ValidateEventOrder(page.Data, watermark, pageNumber + 1);
            IReadOnlyList<ABEvent> events = page.Data;

            if (plan.MaxEvents is int cap && state.EventsRead + events.Count > cap)
            {
                // The service returned more rows than the limit asked for. Keep only what fits under
                // the ceiling; the discarded events are still past the watermark being reported.
                int keep = cap - state.EventsRead;
                List<ABEvent> kept = new(keep);
                for (int index = 0; index < keep; index++)
                {
                    kept.Add(events[index]);
                }

                events = kept.AsReadOnly();
                highestSequence = SequenceOf(events[^1], events.Count - 1, pageNumber + 1);
            }

            pageNumber++;
            state.EventsRead += events.Count;
            state.HighestSequence = highestSequence;
            watermark = highestSequence;

            yield return new EventPage(events, pageNumber, state.ReportedTotalCount, highestSequence);

            if (plan.MaxEvents is int reached && state.EventsRead >= reached)
            {
                // The ceiling stopped the read. Whether the feed also happened to end here is
                // unknowable without another request, so the read reports itself incomplete and the
                // caller's next read from this watermark settles it.
                state.IsComplete = false;
                yield break;
            }
        }
    }

    /// <summary>
    /// The shared document traversal. Both public document methods run this walk; the buffering one
    /// verifies completeness afterwards from the shared <paramref name="state"/>.
    /// </summary>
    /// <param name="documentGuid">The validated document GUID.</param>
    /// <param name="plan">The validated read plan.</param>
    /// <param name="state">Accumulates what the completeness checks need. Mutated in place.</param>
    /// <param name="cancellationToken">Cancels the traversal.</param>
    /// <returns>The non-empty pages of the traversal, in order.</returns>
    private async IAsyncEnumerable<StandardPage> WalkDocumentAsync(
        string documentGuid,
        DocumentWalkPlan plan,
        DocumentWalkState state,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        StandardsFilter filter = StandardsFilter.ByDocument(documentGuid);
        int offset = 0;
        int pageNumber = 0;
        int maxRequests = int.MaxValue;

        while (true)
        {
            if (state.RequestsIssued >= maxRequests)
            {
                throw new ABConnectPagingException(
                    $"The traversal of document '{documentGuid}' has issued its whole budget of " +
                    $"{maxRequests} requests without accounting for the {state.ReportedTotalCount} " +
                    $"standards AB Connect reported, having collected {state.RowsCollected}. The " +
                    "budget is ceil(count / limit) + 2 requests; exceeding it means the result set " +
                    "is shifting under the walk rather than that the document is large.");
            }

            StandardsQuery query = new()
            {
                Filter = filter,
                Fields = plan.Fields,
                Sort = StandardSort.Default,
                Page = new PageRequest(offset, plan.PageSize),
                Status = plan.Status,
            };

            ABPage<Standard> page = await _client.GetStandardsAsync(query, cancellationToken)
                .ConfigureAwait(false);
            state.RequestsIssued++;

            // AB Connect echoes the limit it actually applied, which can be lower than the one
            // requested. The offset arithmetic and the request budget both follow the echo.
            int appliedLimit = page.Meta.Limit > 0 ? page.Meta.Limit : plan.PageSize;

            if (state.RequestsIssued == 1)
            {
                state.ReportedTotalCount = page.Meta.Count;
                maxRequests = MaxRequestsFor(page.Meta.Count, appliedLimit);
            }

            IReadOnlyList<Standard> rows = page.Data;

            if (rows.Count > 0)
            {
                pageNumber++;
                RecordIdentities(rows, state, documentGuid, pageNumber);
                state.RowsCollected += rows.Count;

                yield return new StandardPage(rows, pageNumber, state.ReportedTotalCount);
            }

            if (rows.Count < appliedLimit || state.RowsCollected >= state.ReportedTotalCount)
            {
                // A short page is the last page, and reaching the reported count is the other way to
                // be done. Either way the walk stops here; whether it gathered everything is decided
                // by the completeness checks, not by guesswork inside the loop.
                yield break;
            }

            offset += appliedLimit;
        }
    }

    /// <summary>
    /// Issues the single one-row request both probes share.
    /// </summary>
    /// <param name="filter">The scope of the probe.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The matched standard, or null when nothing matched.</returns>
    private async Task<Standard?> ProbeAsync(StandardsFilter filter, CancellationToken cancellationToken)
    {
        StandardsQuery query = new()
        {
            Filter = filter,
            Fields = StandardFieldSet.Probe,
            Sort = StandardSort.Default,
            Page = new PageRequest(0, 1),
            Status = StandardStatusScope.ActiveAndDeleted,
        };

        ABPage<Standard> page = await _client.GetStandardsAsync(query, cancellationToken)
            .ConfigureAwait(false);

        return page.Data.Count == 0 ? null : page.Data[0];
    }

    /// <summary>
    /// Applies the three ordering guards to one page of events and returns the page's highest
    /// sequence.
    /// </summary>
    /// <param name="events">The page's events. Never empty.</param>
    /// <param name="watermark">The sequence the page was requested with.</param>
    /// <param name="pageNumber">The one-based page number, for the failure messages.</param>
    /// <returns>The highest sequence on the page, which becomes the next watermark.</returns>
    /// <exception cref="ABConnectPagingException">Any of the three guards failed.</exception>
    private static long ValidateEventOrder(IReadOnlyList<ABEvent> events, long watermark, int pageNumber)
    {
        // Guard 1: strictly ascending within the page. AB Connect's default ordering is by relevance,
        // so sort[events]=seq is requested on every call; a descending or repeated sequence means the
        // service did not honor it, and the walk would then advance its watermark past events it
        // never saw.
        for (int index = 1; index < events.Count; index++)
        {
            long previous = SequenceOf(events[index - 1], index - 1, pageNumber);
            long current = SequenceOf(events[index], index, pageNumber);

            if (current <= previous)
            {
                throw new ABConnectPagingException(
                    $"AB Connect returned event sequence {current} immediately after sequence " +
                    $"{previous} on page {pageNumber}, so the page is not in ascending sequence " +
                    "order and sort[events]=seq was not honored. Advancing a watermark across this " +
                    "page would skip events permanently.");
            }
        }

        long lowestSequence = SequenceOf(events[0], 0, pageNumber);
        long highestSequence = SequenceOf(events[^1], events.Count - 1, pageNumber);

        // Guard 2: the walk must advance. A page that carries events but whose highest sequence does
        // not exceed the sequence it was requested with would be requested again forever.
        if (highestSequence <= watermark)
        {
            throw new ABConnectPagingException(
                $"AB Connect returned {events.Count} event(s) on page {pageNumber} for the filter " +
                $"(seq GT {watermark}) whose highest sequence is {highestSequence}, so the " +
                "traversal cannot advance past the watermark and would repeat this request forever.");
        }

        // Guard 3: no stale row. The filter asked for sequences strictly greater than the watermark,
        // so a row at or below it means the filter was not applied and the page cannot be trusted.
        if (lowestSequence <= watermark)
        {
            throw new ABConnectPagingException(
                $"AB Connect returned event sequence {lowestSequence} on page {pageNumber} for the " +
                $"filter (seq GT {watermark}), so the sequence filter was not applied and the page " +
                "carries events the caller has already seen.");
        }

        return highestSequence;
    }

    /// <summary>
    /// Reads one event's sequence.
    /// </summary>
    /// <param name="item">The event.</param>
    /// <param name="indexOnPage">The event's zero-based index on its page, for the failure message.</param>
    /// <param name="pageNumber">The one-based page number, for the failure message.</param>
    /// <returns>The event's sequence.</returns>
    /// <exception cref="ABConnectPagingException">The event carries no attributes and therefore no sequence.</exception>
    private static long SequenceOf(ABEvent item, int indexOnPage, int pageNumber)
    {
        ABEventAttributes? attributes = item.Attributes;

        if (attributes is null)
        {
            throw new ABConnectPagingException(
                $"AB Connect returned the event at index {indexOnPage} of page {pageNumber} with no " +
                "attributes block, so it has no sequence and the traversal has no safe watermark to " +
                "advance to.");
        }

        return attributes.Seq;
    }

    /// <summary>
    /// Runs the duplicate check over one page and remembers the identities it saw.
    /// </summary>
    /// <param name="rows">The page's standards. Never empty.</param>
    /// <param name="state">The walk state carrying the identities seen so far.</param>
    /// <param name="documentGuid">The document being read, for the failure message.</param>
    /// <param name="pageNumber">The one-based page number, for the failure message.</param>
    /// <exception cref="ABConnectPagingException">A standard was returned twice.</exception>
    private static void RecordIdentities(
        IReadOnlyList<Standard> rows,
        DocumentWalkState state,
        string documentGuid,
        int pageNumber)
    {
        foreach (Standard row in rows)
        {
            string identity = IdentityOf(row);

            if (!state.Identities.Add(identity))
            {
                throw new ABConnectPagingException(
                    $"AB Connect returned standard '{identity}' twice while reading document " +
                    $"'{documentGuid}', the second time on page {pageNumber}. A duplicate means the " +
                    "result set shifted under the offset walk, so the rows gathered so far are not a " +
                    "consistent snapshot and the document must be refetched.");
            }
        }
    }

    /// <summary>
    /// The value that identifies a standard for the duplicate and shortfall checks.
    /// </summary>
    /// <param name="standard">The standard.</param>
    /// <returns>
    /// <c>attributes.guid</c> when the requested field set carried it, and the JSON:API resource id
    /// otherwise, so narrowing the field set does not weaken the checks.
    /// </returns>
    private static string IdentityOf(Standard standard)
    {
        string? guid = standard.Attributes?.Guid;
        return string.IsNullOrWhiteSpace(guid) ? standard.Id : guid;
    }

    /// <summary>
    /// The request budget for a document traversal: <c>ceil(count / limit) + 2</c>.
    /// </summary>
    /// <param name="reportedCount">The count AB Connect reported on the first page.</param>
    /// <param name="appliedLimit">The rows-per-page AB Connect actually applied. At least one.</param>
    /// <returns>The maximum number of requests the traversal is allowed to issue.</returns>
    private static int MaxRequestsFor(int reportedCount, int appliedLimit)
    {
        if (reportedCount <= 0)
        {
            return 2;
        }

        long pages = ((long)reportedCount + appliedLimit - 1) / appliedLimit;
        return (int)Math.Min(pages + 2, int.MaxValue);
    }

    /// <summary>
    /// Runs the duplicate and shortfall checks that make a snapshot a snapshot.
    /// </summary>
    /// <param name="documentGuid">The document that was read.</param>
    /// <param name="rowsCollected">The number of rows gathered across every page.</param>
    /// <param name="state">The walk state carrying the distinct identities and the reported count.</param>
    /// <exception cref="ABConnectPagingException">Either check failed.</exception>
    private static void VerifySnapshotCompleteness(string documentGuid, int rowsCollected, DocumentWalkState state)
    {
        // Check 1, restated as the postcondition rather than as the per-page detection that already
        // ran during the walk: distinct GUIDs equal rows collected.
        if (state.Identities.Count != rowsCollected)
        {
            throw new ABConnectPagingException(
                $"The traversal of document '{documentGuid}' gathered {rowsCollected} rows but only " +
                $"{state.Identities.Count} distinct standards, so the result set shifted under the " +
                "offset walk and the rows are not a consistent snapshot.");
        }

        // Check 2, tolerance zero. A document edited between the first page and the last lands here,
        // and failing is the correct outcome: refetch rather than write an inconsistent snapshot.
        if (state.Identities.Count != state.ReportedTotalCount)
        {
            throw new ABConnectPagingException(
                $"The traversal of document '{documentGuid}' gathered {state.Identities.Count} " +
                $"distinct standards but AB Connect reported {state.ReportedTotalCount} on the first " +
                "page. The tolerance is zero. The usual cause is an edit to the document between the " +
                "first page and the last, which shifts rows across page boundaries; the snapshot is " +
                "not internally consistent, must not be diffed against a local mirror, and the " +
                "document should be refetched.");
        }
    }

    /// <summary>
    /// Validates event read options eagerly and resolves the effective page size.
    /// </summary>
    /// <param name="options">The caller's options, or null for the defaults.</param>
    /// <returns>The plan the walk runs from.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The page size or the event ceiling is below one.</exception>
    private EventWalkPlan CreateEventWalkPlan(EventReadOptions? options)
    {
        EventReadOptions effective = options ?? new EventReadOptions();

        if (effective.PageSize < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                effective.PageSize,
                "EventReadOptions.PageSize must be at least 1. A page size of zero returns the meta " +
                "block and no rows, which would report an empty feed rather than read it.");
        }

        if (effective.MaxEvents is int ceiling && ceiling < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                ceiling,
                "EventReadOptions.MaxEvents must be at least 1 when it is set. Leave it null for no " +
                "ceiling.");
        }

        return new EventWalkPlan(ResolvePageSize(effective.PageSize), effective.MaxEvents, effective.StandardScope);
    }

    /// <summary>
    /// Validates document read options eagerly and resolves the effective page size.
    /// </summary>
    /// <param name="options">The caller's options, or null for the defaults.</param>
    /// <returns>The plan the walk runs from.</returns>
    /// <exception cref="ArgumentException">The field set is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page size is below one.</exception>
    private DocumentWalkPlan CreateDocumentWalkPlan(DocumentReadOptions? options)
    {
        DocumentReadOptions effective = options ?? new DocumentReadOptions();

        if (effective.Fields is null)
        {
            throw new ArgumentException(
                "DocumentReadOptions.Fields must not be null. Use StandardFieldSet.Snapshot for the " +
                "full mirror set.",
                nameof(options));
        }

        if (effective.PageSize < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                effective.PageSize,
                "DocumentReadOptions.PageSize must be at least 1. A page size of zero returns the " +
                "meta block and no rows, which would report an empty document rather than read it.");
        }

        return new DocumentWalkPlan(effective.Fields, effective.Status, ResolvePageSize(effective.PageSize));
    }

    /// <summary>
    /// Lowers a requested page size to what a request can actually carry.
    /// </summary>
    /// <param name="requested">The caller's page size. At least one.</param>
    /// <returns>
    /// The smaller of the request, AB Connect's documented maximum, and
    /// <see cref="ABConnectOptions.PageSize"/>, because the query builder lowers every limit to the
    /// configured page size and the traversal arithmetic has to match what is actually sent.
    /// </returns>
    private int ResolvePageSize(int requested)
        => Math.Min(
            Math.Min(requested, PageRequest.MaxLimit),
            Math.Clamp(_options.PageSize, 1, PageRequest.MaxLimit));

    /// <summary>The resolved inputs of one events traversal.</summary>
    /// <param name="PageSize">The effective rows per request.</param>
    /// <param name="MaxEvents">The ceiling on events read, or null for none.</param>
    /// <param name="StandardScope">The optional restriction on the standards the events concern.</param>
    private sealed record EventWalkPlan(int PageSize, int? MaxEvents, StandardsFilter? StandardScope);

    /// <summary>The resolved inputs of one document traversal.</summary>
    /// <param name="Fields">The field set every page requests.</param>
    /// <param name="Status">The lifecycle scope every page requests.</param>
    /// <param name="PageSize">The effective rows per request.</param>
    private sealed record DocumentWalkPlan(StandardFieldSet Fields, StandardStatusScope Status, int PageSize);

    /// <summary>
    /// What an events traversal accumulates for the buffering caller. The walk itself only ever
    /// writes here; nothing is read back to make a paging decision except the running event count.
    /// </summary>
    private sealed class EventWalkState
    {
        /// <summary>How many HTTP requests the traversal issued, including the terminal empty one.</summary>
        public int RequestsIssued { get; set; }

        /// <summary>How many events the traversal has yielded so far.</summary>
        public int EventsRead { get; set; }

        /// <summary>The count AB Connect reported on the first request.</summary>
        public int ReportedTotalCount { get; set; }

        /// <summary>The highest sequence yielded, or null while nothing has been yielded.</summary>
        public long? HighestSequence { get; set; }

        /// <summary>Whether the traversal reached the end of the feed rather than a ceiling.</summary>
        public bool IsComplete { get; set; } = true;
    }

    /// <summary>What a document traversal accumulates for the completeness checks.</summary>
    private sealed class DocumentWalkState
    {
        /// <summary>
        /// The distinct standard identities seen so far, compared case-insensitively because AB
        /// Connect's GUID casing is not guaranteed to be stable across pages.
        /// </summary>
        public HashSet<string> Identities { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>How many HTTP requests the traversal issued.</summary>
        public int RequestsIssued { get; set; }

        /// <summary>How many rows the traversal gathered across every page.</summary>
        public int RowsCollected { get; set; }

        /// <summary>The count AB Connect reported on the first request.</summary>
        public int ReportedTotalCount { get; set; }
    }
}
