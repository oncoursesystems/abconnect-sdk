namespace OnCourse.ABConnect.Queries;

/// <summary>
/// A request for the values of one facet on the standards endpoint.
/// </summary>
/// <remarks>
/// A facet query always emits <c>limit=0</c> and <c>facet_summary=</c><see cref="FacetName"/>, so it
/// returns facet values and no standards. AB Connect does not support paging of facet data, and
/// truncates a facet with more than 10,000 values, which is why the result exposes both the reported
/// total and the returned values.
/// </remarks>
/// <typeparam name="TValue">The detail type each facet value deserializes into.</typeparam>
public sealed record FacetQuery<TValue>
{
    /// <summary>
    /// The AB Connect facet name to summarize, for example <c>document.publication</c> or
    /// <c>section</c>.
    /// </summary>
    public required string FacetName { get; init; }

    /// <summary>
    /// An optional restriction on the standards the facet is computed over. Null and
    /// <see cref="StandardsFilter.None"/> both mean every licensed standard.
    /// </summary>
    public StandardsFilter? Filter { get; init; }
}
