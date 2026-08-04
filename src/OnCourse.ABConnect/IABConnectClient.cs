using OnCourse.ABConnect.Models;
using OnCourse.ABConnect.Queries;
using OnCourse.ABConnect.Throttling;

namespace OnCourse.ABConnect;

/// <summary>
/// The low-level AB Connect client. Each method performs exactly one HTTP request and returns
/// exactly what came back. It does no paging and no retry beyond the transport pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The value-or-throw guarantee, this package's headline promise. For every method on <see cref="IABConnectClient"/> and
/// <see cref="IABConnectFeed"/>: the method either returns a non-null value that was deserialized
/// from a 2xx response body, or it throws. It never returns null, and it never returns a
/// default-constructed envelope. An <c>ABPage&lt;T&gt;</c> with <c>Data.Count == 0</c> therefore
/// means, unambiguously, that AB Connect answered successfully and matched nothing.
/// </para>
/// <para>
/// Every failure is an <see cref="ABConnectException"/>. Catch
/// <see cref="ABConnectNotLicensedException"/> to skip data outside the account's license, which is
/// a documented normal outcome during event processing, and
/// <see cref="ABConnectRequestException"/> to handle any request failure uniformly. A cancellation
/// the caller requested surfaces as <see cref="OperationCanceledException"/>, unwrapped, per .NET
/// convention.
/// </para>
/// <para>
/// Paging, ordering, and completeness live on <see cref="IABConnectFeed"/>, not here. This interface
/// is deliberately a thin HTTP shim so that it is trivially mockable and the interesting logic can
/// be tested with no HTTP at all.
/// </para>
/// </remarks>
public interface IABConnectClient
{
    /// <summary>
    /// Reads one page of standards.
    /// </summary>
    /// <param name="query">The query to run.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// One page. Never null; an empty <c>Data</c> list means the query matched nothing, per
    /// the value-or-throw guarantee.
    /// </returns>
    /// <exception cref="ABConnectRequestException">The request failed or its body could not be read.</exception>
    /// <exception cref="ABConnectConfigurationException">The query requests wildcard fields while <see cref="ABConnectOptions.AllowWildcardFields"/> is false.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<ABPage<Standard>> GetStandardsAsync(
        StandardsQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a single standard by AB Connect GUID. Returns deleted standards as well as active ones,
    /// which makes this the documented remedy for a stale local copy and the only way to resolve a
    /// retired GUID.
    /// </summary>
    /// <param name="guid">The AB Connect GUID of the standard.</param>
    /// <param name="fields">The fields to request, or null for <see cref="StandardFieldSet.Snapshot"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The standard. Never null, per the value-or-throw guarantee.</returns>
    /// <exception cref="ABConnectNotFoundException">The GUID is unknown.</exception>
    /// <exception cref="ABConnectNotLicensedException">The GUID is valid but falls outside the account's license.</exception>
    /// <exception cref="ArgumentException"><paramref name="guid"/> is empty or is not a well-formed GUID.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<Standard> GetStandardAsync(
        string guid,
        StandardFieldSet? fields = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one page of change events.
    /// </summary>
    /// <param name="query">The query to run, including the watermark to read past.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// One page. Never null; an empty <c>Data</c> list means there is nothing past the watermark,
    /// per the value-or-throw guarantee.
    /// </returns>
    /// <exception cref="ABConnectRequestException">The request failed or its body could not be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<ABPage<ABEvent>> GetEventsAsync(
        EventsQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the values of one facet on the standards endpoint, requested with <c>limit=0</c>.
    /// </summary>
    /// <typeparam name="TValue">The detail type each facet value deserializes into.</typeparam>
    /// <param name="query">The facet to summarize and the standards to summarize it over.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The facet. Never null, per the value-or-throw guarantee. Check <c>IsTruncated</c> before treating the values
    /// as complete: AB Connect returns at most the first 10,000 entries and does not page facets.
    /// </returns>
    /// <exception cref="ABConnectRequestException">The request failed or its body could not be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<ABFacet<TValue>> GetFacetAsync<TValue>(
        FacetQuery<TValue> query,
        CancellationToken cancellationToken = default)
        where TValue : class;

    /// <summary>Reads the region facet.</summary>
    /// <param name="filter">An optional restriction on the standards the facet is computed over.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The region facet. Never null, per the value-or-throw guarantee.</returns>
    /// <exception cref="ABConnectRequestException">The request failed or its body could not be read.</exception>
    Task<ABFacet<Region>> GetRegionFacetAsync(
        StandardsFilter? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the authority facet.</summary>
    /// <param name="filter">An optional restriction on the standards the facet is computed over.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The authority facet. Never null, per the value-or-throw guarantee.</returns>
    /// <exception cref="ABConnectRequestException">The request failed or its body could not be read.</exception>
    Task<ABFacet<Authority>> GetAuthorityFacetAsync(
        StandardsFilter? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the publication facet.</summary>
    /// <param name="filter">An optional restriction on the standards the facet is computed over.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The publication facet. Never null, per the value-or-throw guarantee.</returns>
    /// <exception cref="ABConnectRequestException">The request failed or its body could not be read.</exception>
    Task<ABFacet<Publication>> GetPublicationFacetAsync(
        StandardsFilter? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the document facet.</summary>
    /// <param name="filter">An optional restriction on the standards the facet is computed over.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The document facet. Never null, per the value-or-throw guarantee. The detail objects are thin: description,
    /// adopt year, and GUID only. Probe for a document to get the rest.
    /// </returns>
    /// <exception cref="ABConnectRequestException">The request failed or its body could not be read.</exception>
    Task<ABFacet<DocumentSummary>> GetDocumentFacetAsync(
        StandardsFilter? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the section facet.</summary>
    /// <param name="filter">An optional restriction on the standards the facet is computed over.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The section facet. Never null, per the value-or-throw guarantee.</returns>
    /// <exception cref="ABConnectRequestException">The request failed or its body could not be read.</exception>
    Task<ABFacet<SectionSummary>> GetSectionFacetAsync(
        StandardsFilter? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Live throttle counters, for a progress display. Reads of this state never block and never
    /// affect the bucket.
    /// </summary>
    IABConnectThrottleState Throttle { get; }
}
