using OnCourse.ABConnect.Models;

namespace OnCourse.ABConnect.Feed;

/// <summary>
/// Every event past a watermark, buffered into one result.
/// </summary>
/// <remarks>
/// The value-or-throw guarantee applies: a returned batch was assembled entirely from 2xx response bodies, or the
/// read threw. An empty <see cref="Events"/> list means the service answered and there is nothing
/// past the watermark, never that something failed.
/// </remarks>
public sealed record EventBatch
{
    /// <summary>
    /// The events, in ascending sequence order. Never null; empty means nothing past the watermark.
    /// </summary>
    public required IReadOnlyList<ABEvent> Events { get; init; }

    /// <summary>The watermark that was requested. Events are strictly greater than this value.</summary>
    public required long AfterSequence { get; init; }

    /// <summary>
    /// The highest sequence in <see cref="Events"/>, which is the watermark to persist. Null if and
    /// only if <see cref="Events"/> is empty, in which case the caller keeps its existing watermark.
    /// </summary>
    public long? HighestSequence { get; init; }

    /// <summary>
    /// The total matching event count AB Connect reported on the first page. Compare against
    /// <c>Events.Count</c> to see how much of the feed this batch covers.
    /// </summary>
    public required int ReportedTotalCount { get; init; }

    /// <summary>
    /// How many HTTP requests were issued to assemble this batch.
    /// </summary>
    /// <remarks>
    /// A completed traversal costs one request more than the number of pages that carried events,
    /// because the walk stops only when a request comes back with none. It deliberately does not stop
    /// on a short page or on a reported count: either would silently truncate a delta if AB Connect
    /// under-reported, and a watermark that has moved past an event can never be walked back.
    /// </remarks>
    public required int PagesFetched { get; init; }

    /// <summary>
    /// Whether the traversal reached the end of the feed. False only when
    /// <see cref="EventReadOptions.MaxEvents"/> stopped it, in which case the caller should read again
    /// from <see cref="HighestSequence"/>.
    /// </summary>
    /// <remarks>
    /// A ceiling that happens to coincide with the end of the feed also reports false, because
    /// whether anything remains cannot be known without another request and claiming completeness on
    /// a guess is how a delta silently loses events. The caller's next read from
    /// <see cref="HighestSequence"/> then returns no events and confirms the feed is drained. False
    /// is never a failure: a truncated batch is a legitimate operational choice, and everything in
    /// <see cref="Events"/> is real.
    /// </remarks>
    public required bool IsComplete { get; init; }
}
