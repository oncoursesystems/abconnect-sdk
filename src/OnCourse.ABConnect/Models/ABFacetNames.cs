namespace OnCourse.ABConnect.Models;

/// <summary>
/// The AB Connect facet names this SDK requests on the standards endpoint.
/// </summary>
/// <remarks>
/// <para>
/// These strings are dotted property paths into the standards resource, and AB Connect echoes the
/// requested path back as the facet's own name in the response. They live inside the SDK on purpose.
/// Before version 3 every consumer carried its own copy, matching a literal against the response's
/// facet name at each call site, so a typo produced an empty result rather than an error and a change
/// on AB Connect's side had to be fixed in every consumer independently.
/// </para>
/// <para>
/// A facet name is not a closed set: AB Connect will facet on other properties, which is what
/// <c>FacetQuery&lt;TValue&gt;.FacetName</c> is for. These are the five this system uses, each with a
/// convenience wrapper on <c>IABConnectClient</c>.
/// </para>
/// </remarks>
public static class ABFacetNames
{
    /// <summary>
    /// The regions a publication applies to, faceted through the document. Projects into
    /// <see cref="Region"/>.
    /// </summary>
    public const string Regions = "document.publication.regions";

    /// <summary>
    /// The authorities that own a publication, faceted through the document. Projects into
    /// <see cref="Authority"/>.
    /// </summary>
    public const string Authorities = "document.publication.authorities";

    /// <summary>
    /// The publications the matched standards belong to. Projects into <see cref="Publication"/>,
    /// which is the thin facet shape, not <see cref="StandardPublication"/>.
    /// </summary>
    public const string Publications = "document.publication";

    /// <summary>
    /// The documents the matched standards belong to. Projects into <see cref="DocumentSummary"/>,
    /// which carries only a GUID, a description, and an adopt year.
    /// </summary>
    public const string Documents = "document";

    /// <summary>
    /// The sections the matched standards belong to. Projects into <see cref="SectionSummary"/>.
    /// </summary>
    /// <remarks>
    /// AB Connect returns at most the first 10,000 values of any facet and does not page facet data,
    /// and this is the facet most likely to exceed that: AB Connect's own sample license already shows
    /// 5,542 sections. Always check <see cref="ABFacet{TValue}.IsTruncated"/> on a section facet, and
    /// derive the hierarchy from standards rows instead when it is true.
    /// </remarks>
    public const string Sections = "section";
}
