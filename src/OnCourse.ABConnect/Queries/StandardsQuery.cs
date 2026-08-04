namespace OnCourse.ABConnect.Queries;

/// <summary>
/// A single request for one page of standards. Immutable; build variants with <c>with</c>.
/// </summary>
/// <remarks>
/// Every default on this type is the safe production choice: an explicit field set rather than a
/// wildcard, a total ordering rather than relevance ordering, and a status scope that includes
/// deletions so a mirror can see them.
/// </remarks>
public sealed record StandardsQuery
{
    /// <summary>Which standards to return. Defaults to every licensed standard.</summary>
    public StandardsFilter Filter { get; init; } = StandardsFilter.None;

    /// <summary>Which fields to return. Defaults to the full mirror set.</summary>
    public StandardFieldSet Fields { get; init; } = StandardFieldSet.Snapshot;

    /// <summary>
    /// The ordering to request. Defaults to <c>seq</c> then <c>guid</c>, a total order that makes
    /// offset paging safe.
    /// </summary>
    public StandardSort Sort { get; init; } = StandardSort.Default;

    /// <summary>Which page window to return. Defaults to the first page.</summary>
    public PageRequest Page { get; init; } = PageRequest.First;

    /// <summary>
    /// Which lifecycle states to include. Defaults to both active and deleted standards.
    /// </summary>
    public StandardStatusScope Status { get; init; } = StandardStatusScope.ActiveAndDeleted;
}
