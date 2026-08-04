using OnCourse.ABConnect.Queries;

namespace OnCourse.ABConnect.Feed;

/// <summary>Options for reading the standards of one document.</summary>
public sealed record DocumentReadOptions
{
    /// <summary>Which fields to request. Defaults to the full mirror set.</summary>
    public StandardFieldSet Fields { get; init; } = StandardFieldSet.Snapshot;

    /// <summary>
    /// Which lifecycle states to include. Defaults to every documented state, active, deleted and
    /// obsolete, because a mirror that cannot see a deletion cannot apply it and one that drops
    /// obsolete standards silently sheds rows existing local links resolve against.
    /// </summary>
    public StandardStatusScope Status { get; init; } = StandardStatusScope.All;

    /// <summary>Rows per page. Clamped to AB Connect's documented maximum of 100.</summary>
    /// <remarks>
    /// Must be at least one; zero is rejected with <see cref="ArgumentOutOfRangeException"/>, because
    /// a limit of zero returns the <c>meta</c> block and no rows and would report an empty document
    /// rather than read it. The effective value is also lowered to
    /// <see cref="ABConnectOptions.PageSize"/> when the configured page size is smaller, since every
    /// request the SDK sends is subject to that ceiling. The page size also sets the traversal's
    /// request budget, which is <c>ceil(meta.count / limit) + 2</c>.
    /// </remarks>
    public int PageSize { get; init; } = 100;
}
