namespace OnCourse.ABConnect.Models;

/// <summary>
/// One page of a JSON:API list response from AB Connect.
/// </summary>
/// <remarks>
/// The value-or-throw guarantee, this package's headline promise. For every method on <c>IABConnectClient</c> and
/// <c>IABConnectFeed</c>: the method either returns a non-null value that was deserialized from a
/// 2xx response body, or it throws. It never returns null, and it never returns a
/// default-constructed envelope. An <see cref="ABPage{T}"/> with <c>Data.Count == 0</c> therefore
/// means, unambiguously, that AB Connect answered successfully and matched nothing.
/// </remarks>
/// <typeparam name="T">The resource type carried in <see cref="Data"/>.</typeparam>
public sealed record ABPage<T>
{
    /// <summary>
    /// The resources on this page. Never null; may be empty, which by the value-or-throw guarantee means the query
    /// matched nothing rather than that anything went wrong.
    /// </summary>
    public required IReadOnlyList<T> Data { get; init; }

    /// <summary>The page's counters. Never null.</summary>
    public required PageMeta Meta { get; init; }

    /// <summary>The page's navigation links. Never null, though individual URLs may be null.</summary>
    public required PageLinks Links { get; init; }
}

/// <summary>AB Connect's <c>meta</c> block for a list response.</summary>
/// <param name="Limit">The page size that was applied, which may be lower than the one requested.</param>
/// <param name="Offset">The zero-based offset of the first row on this page.</param>
/// <param name="Count">The total number of rows matching the query across all pages. This is what drives progress reporting.</param>
/// <param name="Took">How long AB Connect reports it spent on the query, in milliseconds.</param>
public sealed record PageMeta(int Limit, int Offset, int Count, int Took);

/// <summary>
/// AB Connect's <c>links</c> block for a list response. Every member may be null.
/// </summary>
/// <remarks>
/// Retained for diagnostics even though the SDK's paging helpers deliberately do not follow
/// <see cref="Next"/>: the events traversal re-anchors on sequence rather than on offset, because an
/// offset-based cursor silently skips rows when the underlying result set changes mid-traversal.
/// </remarks>
/// <param name="Self">The URL of this page.</param>
/// <param name="First">The URL of the first page.</param>
/// <param name="Prev">The URL of the previous page, or null on the first page.</param>
/// <param name="Next">The URL of the next page, or null on the last page.</param>
/// <param name="Last">The URL of the last page.</param>
public sealed record PageLinks(string? Self, string? First, string? Prev, string? Next, string? Last);
