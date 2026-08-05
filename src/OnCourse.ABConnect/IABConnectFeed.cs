using OnCourse.ABConnect.Feed;
using OnCourse.ABConnect.Models;

namespace OnCourse.ABConnect;

/// <summary>
/// The sync-oriented facade over <see cref="IABConnectClient"/>. Paging, ordering, and completeness
/// live here.
/// </summary>
/// <remarks>
/// <para>
/// The value-or-throw guarantee, this package's headline promise. For every method on <see cref="IABConnectClient"/> and
/// <see cref="IABConnectFeed"/>: the method either returns a non-null value that was deserialized
/// from a 2xx response body, or it throws. It never returns null, and it never returns a
/// default-constructed envelope. An empty result list therefore means, unambiguously, that AB
/// Connect answered successfully and matched nothing.
/// </para>
/// <para>
/// The two nullable-returning probes are the sole and deliberate exception in spirit rather than in
/// substance: they return null for "AB Connect answered and there is no such document", which is
/// still a value deserialized from a 2xx body, never a swallowed failure.
/// </para>
/// <para>
/// Failures propagate out of an <c>await foreach</c> exactly as they do out of an <c>await</c>. A
/// traversal that fails on its fourth page throws on that iteration; the pages already yielded were
/// real and may be kept.
/// </para>
/// </remarks>
public interface IABConnectFeed
{
    /// <summary>
    /// Reads all events with a sequence strictly greater than the watermark, in ascending sequence
    /// order, buffered into a single result.
    /// </summary>
    /// <param name="afterSequence">The watermark. Only events past this sequence are returned.</param>
    /// <param name="options">Read options, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the traversal.</param>
    /// <returns>
    /// The batch. Never null; an empty <c>Events</c> list means the service answered and there is
    /// nothing past the watermark, per the value-or-throw guarantee.
    /// </returns>
    /// <exception cref="ABConnectRequestException">Any page failed.</exception>
    /// <exception cref="ABConnectPagingException">The service returned events out of ascending sequence order.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<EventBatch> ReadEventsAsync(
        long afterSequence,
        EventReadOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same traversal as <see cref="ReadEventsAsync"/>, surfaced one page at a time so a caller
    /// can persist and advance its watermark incrementally.
    /// </summary>
    /// <param name="afterSequence">The watermark. Only events past this sequence are returned.</param>
    /// <param name="options">Read options, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the traversal.</param>
    /// <returns>
    /// The pages, in order. Yields zero pages when there is nothing past the watermark, and never
    /// yields an empty page.
    /// </returns>
    /// <exception cref="ABConnectRequestException">Any page failed. Thrown from the iteration that fetched it.</exception>
    /// <exception cref="ABConnectPagingException">The service returned events out of ascending sequence order.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    IAsyncEnumerable<EventPage> ReadEventPagesAsync(
        long afterSequence,
        EventReadOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the head of the event feed: the single highest sequence number currently available, in
    /// one request.
    /// </summary>
    /// <remarks>
    /// This is how a cutover establishes its starting watermark. Seeding at the head means the first
    /// delta pull reads only events raised after cutover, instead of replaying the entire history.
    /// The read sorts newest-first and takes one row, so it never walks the feed.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The highest sequence, or <see langword="null"/> when AB Connect answered successfully and the
    /// feed has no events. Null here means "answered, and there are none", never "something failed";
    /// a failure throws.
    /// </returns>
    /// <exception cref="ABConnectRequestException">The request failed or its body could not be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<long?> ReadHeadSequenceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a complete, verified snapshot of every standard in one document.
    /// </summary>
    /// <remarks>
    /// The traversal reconciles the rows it gathered against the count AB Connect reported and fails
    /// rather than return a snapshot it cannot vouch for, so a returned snapshot is safe to diff
    /// against a local mirror, including for deletions.
    /// </remarks>
    /// <param name="documentGuid">The AB Connect GUID of the document.</param>
    /// <param name="options">Read options, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the traversal.</param>
    /// <returns>The snapshot. Never null, per the value-or-throw guarantee.</returns>
    /// <exception cref="ABConnectRequestException">Any page failed.</exception>
    /// <exception cref="ABConnectPagingException">The gathered rows did not reconcile with the reported count.</exception>
    /// <exception cref="ArgumentException"><paramref name="documentGuid"/> is empty or is not a well-formed GUID.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<DocumentSnapshot> ReadDocumentSnapshotAsync(
        string documentGuid,
        DocumentReadOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same traversal as <see cref="ReadDocumentSnapshotAsync"/>, streamed by page, for callers
    /// that will not hold a document in memory.
    /// </summary>
    /// <remarks>
    /// Streaming gives up the completeness guarantee. A caller that stops enumerating early holds an
    /// arbitrary prefix of the document, not a snapshot, and must not diff it for deletions.
    /// </remarks>
    /// <param name="documentGuid">The AB Connect GUID of the document.</param>
    /// <param name="options">Read options, or null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the traversal.</param>
    /// <returns>The pages, in order. Yields zero pages for a document with no matching standards, and never yields an empty page.</returns>
    /// <exception cref="ABConnectRequestException">Any page failed. Thrown from the iteration that fetched it.</exception>
    /// <exception cref="ABConnectPagingException">The service returned a page sequence that violates the requested ordering.</exception>
    /// <exception cref="ArgumentException"><paramref name="documentGuid"/> is empty or is not a well-formed GUID.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    IAsyncEnumerable<StandardPage> ReadDocumentPagesAsync(
        string documentGuid,
        DocumentReadOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes for one fully populated document.
    /// </summary>
    /// <param name="documentGuid">The AB Connect GUID of the document.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The document, or <see langword="null"/> when AB Connect answered successfully and no standard
    /// in the account's license belongs to that document. Null here means "answered, and there is
    /// none", never "something failed"; a failure throws.
    /// </returns>
    /// <exception cref="ABConnectRequestException">The request failed or its body could not be read.</exception>
    /// <exception cref="ArgumentException"><paramref name="documentGuid"/> is empty or is not a well-formed GUID.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<StandardDocument?> ReadDocumentAsync(
        string documentGuid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes for one fully populated publication.
    /// </summary>
    /// <param name="publicationGuid">The AB Connect GUID of the publication.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The publication, or <see langword="null"/> when AB Connect answered successfully and no
    /// standard in the account's license belongs to that publication. Null here means "answered, and
    /// there is none", never "something failed"; a failure throws.
    /// </returns>
    /// <exception cref="ABConnectRequestException">The request failed or its body could not be read.</exception>
    /// <exception cref="ArgumentException"><paramref name="publicationGuid"/> is empty or is not a well-formed GUID.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<StandardPublication?> ReadPublicationAsync(
        string publicationGuid,
        CancellationToken cancellationToken = default);
}
