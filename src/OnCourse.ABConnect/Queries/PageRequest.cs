namespace OnCourse.ABConnect.Queries;

/// <summary>
/// One page window: a zero-based offset and a row limit.
/// </summary>
/// <remarks>
/// The limit is clamped to AB Connect's documented maximum of 100 rather than being rejected,
/// because a caller asking for more rows per round trip is expressing a preference, not an error. A
/// negative offset is rejected: it can only come from arithmetic that has already gone wrong, and
/// silently correcting it is how an offset-paged traversal ends up re-reading page one forever.
/// </remarks>
public sealed record PageRequest
{
    /// <summary>AB Connect's documented maximum rows per page.</summary>
    public const int MaxLimit = 100;

    private static readonly PageRequest FirstPage = new(0, MaxLimit);
    private static readonly PageRequest MetaOnlyPage = new(0, 0);

    /// <summary>Creates a page window.</summary>
    /// <param name="offset">The zero-based index of the first row to return. Must not be negative.</param>
    /// <param name="limit">The maximum rows to return. Clamped to <see cref="MaxLimit"/>. Must not be negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> or <paramref name="limit"/> is negative.</exception>
    public PageRequest(int offset, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        Offset = offset;
        Limit = Math.Min(limit, MaxLimit);
    }

    /// <summary>The zero-based index of the first row this page returns.</summary>
    public int Offset { get; }

    /// <summary>The maximum rows this page returns, never above <see cref="MaxLimit"/>.</summary>
    public int Limit { get; }

    /// <summary>
    /// The first page at the maximum supported size. The query builder lowers the limit to
    /// <see cref="ABConnectOptions.PageSize"/> when the configured page size is smaller, so a caller
    /// that leaves this default in place gets the configured size rather than a hard-coded 100.
    /// </summary>
    public static PageRequest First => FirstPage;

    /// <summary>
    /// Offset zero with a limit of zero, which asks AB Connect for the <c>meta</c> block and no
    /// rows. This is how a facet request and a count probe are expressed.
    /// </summary>
    public static PageRequest MetaOnly => MetaOnlyPage;

    /// <summary>
    /// The window that follows this one, advanced by this page's own <see cref="Limit"/>.
    /// </summary>
    /// <param name="limit">The limit for the next page. Clamped to <see cref="MaxLimit"/>.</param>
    /// <returns>A page window at <c>Offset + Limit</c> with the requested limit.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is negative.</exception>
    public PageRequest Next(int limit) => new(Offset + Limit, limit);
}
