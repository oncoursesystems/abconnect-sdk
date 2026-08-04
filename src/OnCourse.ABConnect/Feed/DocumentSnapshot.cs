using OnCourse.ABConnect.Models;

namespace OnCourse.ABConnect.Feed;

/// <summary>
/// A complete, verified snapshot of every standard in one document.
/// </summary>
/// <remarks>
/// The value-or-throw guarantee applies, and completeness is part of it: the traversal reconciles the rows it
/// gathered against the count AB Connect reported and throws
/// <see cref="ABConnectPagingException"/> rather than returning a snapshot it cannot vouch for. A
/// snapshot in hand is therefore safe to diff against a local mirror, including for deletions.
/// </remarks>
public sealed record DocumentSnapshot
{
    /// <summary>The AB Connect GUID of the document that was read.</summary>
    public required string DocumentGuid { get; init; }

    /// <summary>
    /// Every standard in the document, in the requested sort order. Never null; empty means the
    /// document exists and has no standards matching the requested status scope.
    /// </summary>
    public required IReadOnlyList<Standard> Standards { get; init; }

    /// <summary>
    /// The total matching standard count AB Connect reported, which the traversal has already
    /// reconciled against <c>Standards.Count</c>.
    /// </summary>
    public required int ReportedTotalCount { get; init; }

    /// <summary>
    /// How many HTTP requests were issued to assemble this snapshot. The traversal stops at the first
    /// short page or once it has collected the reported count, and refuses to issue more than
    /// <c>ceil(meta.count / limit) + 2</c> requests.
    /// </summary>
    public required int PagesFetched { get; init; }

    /// <summary>
    /// When the traversal started, in UTC. Anything AB Connect changed after this instant is not in
    /// the snapshot, so this is the instant a subsequent delta should be anchored against.
    /// </summary>
    public required DateTimeOffset FetchedAtUtc { get; init; }
}
