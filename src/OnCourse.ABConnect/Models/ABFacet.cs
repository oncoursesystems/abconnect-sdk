namespace OnCourse.ABConnect.Models;

/// <summary>
/// The values of one facet on the standards endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Facets are a separate shape from <see cref="ABPage{T}"/> because AB Connect treats them
/// differently and the difference must be visible in the API. AB Connect "does not support paging of
/// facet data", and "When requesting facets with a large number of values (count &gt; 10,000) only
/// the first 10,000 entries are returned." Both the reported total and the returned values are
/// therefore exposed, along with <see cref="IsTruncated"/>, so that a caller cannot mistake a
/// truncated facet for a complete one.
/// </para>
/// <para>
/// The value-or-throw guarantee applies: a returned facet was deserialized from a 2xx response body. An empty
/// <see cref="Values"/> list means AB Connect answered successfully and the facet matched nothing.
/// </para>
/// </remarks>
/// <typeparam name="TValue">The detail type carried by each facet value.</typeparam>
public sealed record ABFacet<TValue>
{
    /// <summary>The AB Connect facet name that was requested, for example <c>document.publication</c>.</summary>
    public required string FacetName { get; init; }

    /// <summary>
    /// The total number of distinct values AB Connect reports for this facet, which may exceed the
    /// number of entries it actually returned.
    /// </summary>
    public required int ReportedCount { get; init; }

    /// <summary>The facet values that were returned. Never null; may be empty.</summary>
    public required IReadOnlyList<ABFacetValue<TValue>> Values { get; init; }

    /// <summary>
    /// Whether AB Connect reported more values than it returned. When true, the facet is not a
    /// complete picture and must not be treated as one.
    /// </summary>
    public bool IsTruncated => ReportedCount > Values.Count;
}

/// <summary>One value of a facet, with the number of standards it covers.</summary>
/// <typeparam name="TValue">The detail type for the facet value.</typeparam>
/// <param name="Value">The facet's detail object, as thin as AB Connect returns it inside <c>details</c>.</param>
/// <param name="Count">The number of standards matching the query that carry this facet value.</param>
public sealed record ABFacetValue<TValue>(TValue Value, int Count);
