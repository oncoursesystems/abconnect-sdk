using OnCourse.ABConnect.Models;

namespace OnCourse.ABConnect.Feed;

/// <summary>
/// One page of a streamed document traversal, for callers that will not hold a whole document in
/// memory.
/// </summary>
/// <remarks>
/// Streaming trades the completeness guarantee for a smaller footprint: a caller that stops
/// enumerating early has an arbitrary prefix of the document, not a snapshot, and must not diff it
/// against a local mirror for deletions.
/// </remarks>
/// <param name="Standards">The standards on this page, in the requested sort order. Never null and never empty.</param>
/// <param name="PageNumber">The one-based position of this page within the traversal.</param>
/// <param name="ReportedTotalCount">The total matching standard count AB Connect reported on the first page of the traversal.</param>
public sealed record StandardPage(
    IReadOnlyList<Standard> Standards,
    int PageNumber,
    int ReportedTotalCount);
