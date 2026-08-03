using OnCourse.ABConnect.Models;

namespace OnCourse.ABConnect.Feed;

/// <summary>
/// One page of a streamed event traversal, surfaced so a caller can persist and advance its
/// watermark incrementally rather than buffering the whole feed.
/// </summary>
/// <remarks>
/// A page is never empty: the enumerator finishes instead of yielding one, so
/// <see cref="HighestSequence"/> is always a real sequence taken from a real event.
/// </remarks>
/// <param name="Events">The events on this page, in ascending sequence order. Never null and never empty.</param>
/// <param name="PageNumber">The one-based position of this page within the traversal.</param>
/// <param name="ReportedTotalCount">The total matching event count AB Connect reported on the first page of the traversal.</param>
/// <param name="HighestSequence">The highest sequence on this page, which is the watermark to persist once the page is applied.</param>
public sealed record EventPage(
    IReadOnlyList<ABEvent> Events,
    int PageNumber,
    int ReportedTotalCount,
    long HighestSequence);
