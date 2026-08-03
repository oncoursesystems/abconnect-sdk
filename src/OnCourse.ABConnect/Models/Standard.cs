using System.Text.Json.Serialization;

namespace OnCourse.ABConnect.Models;

/// <summary>
/// One standard, as the JSON:API resource object AB Connect returns.
/// </summary>
/// <remarks>
/// Compare two standards by <see cref="Id"/>, not with <c>==</c>. Record equality is synthesized
/// member by member, and the members that are collections (<see cref="EducationLevels.Grades"/>,
/// <see cref="StandardRelationships.Children"/>, <see cref="StandardPublication.Regions"/>,
/// <see cref="StandardPublication.Authorities"/>) compare by reference, so two standards deserialized
/// from byte-identical JSON are not equal. That is deliberately left alone rather than papered over
/// with a sequence-equality override: the GUID is the identity of a standard, and a deep structural
/// comparison of two snapshots is a diffing decision a consumer should make explicitly against the
/// fields it actually cares about.
/// </remarks>
public sealed record Standard
{
    /// <summary>The JSON:API resource id. Equals the AB Connect GUID for the standard.</summary>
    public required string Id { get; init; }

    /// <summary>The JSON:API resource type, normally <c>standards</c>.</summary>
    public string? Type { get; init; }

    /// <summary>
    /// The standard's attributes, or <see langword="null"/> when the requested field set asked for
    /// no attributes.
    /// </summary>
    public StandardAttributes? Attributes { get; init; }

    /// <summary>
    /// The standard's parent and child references, or <see langword="null"/> when neither
    /// <c>parent</c> nor <c>children</c> was requested or the account is not licensed for them.
    /// </summary>
    public StandardRelationships? Relationships { get; init; }
}

/// <summary>
/// The attribute block of a <see cref="Standard"/>. Every member is nullable: AB Connect returns
/// only the fields the request asked for, and silently omits relationships and fields the account
/// is not licensed for while still returning HTTP 200.
/// </summary>
public sealed record StandardAttributes
{
    /// <summary>The AB Connect GUID of the standard. Duplicates <see cref="Standard.Id"/>.</summary>
    public string? Guid { get; init; }

    /// <summary>
    /// AB Connect's ordering key for the standard within its document. Used as the first sort key
    /// of the default standards sort.
    /// </summary>
    public int? Seq { get; init; }

    /// <summary>The standard's depth in its document hierarchy.</summary>
    public int? Level { get; init; }

    /// <summary>A short human-readable label for the standard.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// The lifecycle status of the standard. Compare against <see cref="ABStandardStatuses"/>; this
    /// is an open string, not a closed enumeration.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// AB Connect's classification of the standard, for example <c>objective</c>. An open string,
    /// not a closed enumeration; AB Connect publishes no complete value set.
    /// </summary>
    public string? StandardType { get; init; }

    /// <summary>
    /// When the standard was last modified, in UTC. Parsed from AB Connect's
    /// <c>yyyy-MM-dd HH:mm:ss</c> form by <see cref="ABConnectDateTimeConverter"/>.
    /// </summary>
    [JsonConverter(typeof(ABConnectDateTimeConverter))]
    public DateTimeOffset? DateModifiedUtc { get; init; }

    /// <summary>
    /// When the standard was deleted, in UTC, or <see langword="null"/> if it has not been deleted.
    /// Parsed from AB Connect's <c>yyyy-MM-dd HH:mm:ss</c> form by
    /// <see cref="ABConnectDateTimeConverter"/>.
    /// </summary>
    [JsonConverter(typeof(ABConnectDateTimeConverter))]
    public DateTimeOffset? DateDeletedUtc { get; init; }

    /// <summary>The standard's number variants. See <see cref="StandardNumber"/> before relying on any of them.</summary>
    public StandardNumber? Number { get; init; }

    /// <summary>The standard's descriptive text.</summary>
    public StandardStatement? Statement { get; init; }

    /// <summary>The grade levels the standard applies to.</summary>
    public EducationLevels? EducationLevels { get; init; }

    /// <summary>The section of the document the standard belongs to.</summary>
    public StandardSection? Section { get; init; }

    /// <summary>The document the standard belongs to, including its publication when requested.</summary>
    public StandardDocument? Document { get; init; }
}

/// <summary>
/// Four of the five number variants AB Connect documents. Every one is nullable and none is a key.
/// </summary>
/// <remarks>
/// <para>
/// Two facts about these variants must be understood before any of them is used. First, AB Connect
/// never defines any of them anywhere in its published documentation; the only observable behavior
/// comes from worked examples and live sampling, in which <see cref="Enhanced"/> and
/// <see cref="PrefixEnhanced"/> were character-for-character identical, and <see cref="Raw"/> was a
/// short unqualified token that is sometimes null.
/// </para>
/// <para>
/// Second, numbers are not identifiers. AB Connect states plainly: "Be aware that numbers are not
/// guaranteed to be unique and some Standards do not have numbers." Never key a local record on any
/// of these; key on the GUID.
/// </para>
/// <para>
/// <c>number.alternate</c>, the fifth variant AB Connect names in its filtering documentation, is
/// deliberately omitted from this model. It was requested successfully (HTTP 200) but returned
/// <c>null</c> on all 750 standards sampled across the full live range, so it is dropped rather
/// than carried as a permanently-null placeholder. A partner or license that does populate it would
/// require this property to be restored.
/// </para>
/// </remarks>
public sealed record StandardNumber
{
    /// <summary>The unqualified number token as authored. Often a short fragment, and sometimes null.</summary>
    public string? Raw { get; init; }

    /// <summary>
    /// AB Connect's qualified number. Observed identical to <see cref="PrefixEnhanced"/> in live
    /// sampling, but the vendor documents no relationship between them, so the two are carried
    /// separately.
    /// </summary>
    public string? Enhanced { get; init; }

    /// <summary>
    /// AB Connect's prefixed qualified number. Observed identical to <see cref="Enhanced"/> in live
    /// sampling; carried separately for the reason given there.
    /// </summary>
    public string? PrefixEnhanced { get; init; }

    /// <summary>
    /// AB Connect's root-qualified number, for example <c>WV.CCRS.SOC.9-12.PF.SS.C.33.b</c>. Named
    /// only in prose in the vendor documentation and never shown in a worked payload; confirmed
    /// populated on most rows by live sampling.
    /// </summary>
    public string? RootEnhanced { get; init; }
}

/// <summary>The descriptive text of a standard.</summary>
public sealed record StandardStatement
{
    /// <summary>The standard's own description.</summary>
    [JsonPropertyName("descr")]
    public string? Description { get; init; }

    /// <summary>
    /// The standard's description combined with the text inherited from its ancestors, which is what
    /// makes a leaf standard readable on its own.
    /// </summary>
    [JsonPropertyName("combined_descr")]
    public string? CombinedDescription { get; init; }
}

/// <summary>A section of a document, as embedded in a standard.</summary>
public sealed record StandardSection
{
    /// <summary>The AB Connect GUID of the section.</summary>
    public string? Guid { get; init; }

    /// <summary>The section's description.</summary>
    [JsonPropertyName("descr")]
    public string? Description { get; init; }
}

/// <summary>The grade levels a standard applies to.</summary>
public sealed record EducationLevels
{
    /// <summary>
    /// The grades the standard applies to. <see langword="null"/> means the array was absent from
    /// the response, which happens when the field was not requested or the account is not licensed
    /// for it; an empty list means the array was present and empty. The distinction is load-bearing
    /// and is preserved everywhere in this model.
    /// </summary>
    public IReadOnlyList<Grade>? Grades { get; init; }

    /// <summary>
    /// The grade with the lowest <see cref="Grade.Seq"/>, or <see langword="null"/> when
    /// <see cref="Grades"/> is null or empty. Grades with a null sequence are ignored for the
    /// comparison and are only returned when no grade has one.
    /// </summary>
    public Grade? Lowest => SelectBySequence(ascending: true);

    /// <summary>
    /// The grade with the highest <see cref="Grade.Seq"/>, or <see langword="null"/> when
    /// <see cref="Grades"/> is null or empty. Grades with a null sequence are ignored for the
    /// comparison and are only returned when no grade has one.
    /// </summary>
    public Grade? Highest => SelectBySequence(ascending: false);

    private Grade? SelectBySequence(bool ascending)
    {
        if (Grades is null || Grades.Count == 0)
        {
            return null;
        }

        Grade? best = null;
        foreach (Grade candidate in Grades)
        {
            if (candidate.Seq is null)
            {
                best ??= candidate;
                continue;
            }

            if (best?.Seq is null)
            {
                best = candidate;
                continue;
            }

            bool better = ascending ? candidate.Seq < best.Seq : candidate.Seq > best.Seq;
            if (better)
            {
                best = candidate;
            }
        }

        return best;
    }
}

/// <summary>One grade level.</summary>
/// <param name="Seq">AB Connect's ordering key for the grade. This is the documented way to order grades; do not rely on array order.</param>
/// <param name="Code">The grade code, for example <c>09</c> or <c>KG</c>.</param>
/// <param name="Guid">The AB Connect GUID of the grade.</param>
/// <param name="Description">The grade's description.</param>
public sealed record Grade(
    int? Seq,
    string? Code,
    string? Guid,
    [property: JsonPropertyName("descr")] string? Description);

/// <summary>
/// A standards document, as embedded in a standard or returned by a document probe.
/// </summary>
public sealed record StandardDocument
{
    /// <summary>The AB Connect GUID of the document.</summary>
    public string? Guid { get; init; }

    /// <summary>The document's description, which is its title.</summary>
    [JsonPropertyName("descr")]
    public string? Description { get; init; }

    /// <summary>The year the document was adopted. A string, because AB Connect never documents it as numeric.</summary>
    public string? AdoptYear { get; init; }

    /// <summary>The year the document was revised. A string, for the reason given on <see cref="AdoptYear"/>.</summary>
    public string? RevisionYear { get; init; }

    /// <summary>The year the document takes effect. A string, for the reason given on <see cref="AdoptYear"/>.</summary>
    public string? ImplementationYear { get; init; }

    /// <summary>The year the document is assessed against. A string, for the reason given on <see cref="AdoptYear"/>.</summary>
    public string? AssessmentYear { get; init; }

    /// <summary>
    /// The document's obsolete year. AB Connect defines no semantics for this field anywhere in its
    /// documentation, so no consumer should infer retirement, deprecation, or any other lifecycle
    /// state from it. A string, for the reason given on <see cref="AdoptYear"/>.
    /// </summary>
    public string? ObsoleteYear { get; init; }

    /// <summary>The publisher's URL for the document.</summary>
    public string? SourceUrl { get; init; }

    /// <summary>
    /// When the document was last modified, in UTC. Parsed from AB Connect's
    /// <c>yyyy-MM-dd HH:mm:ss</c> form by <see cref="ABConnectDateTimeConverter"/>.
    /// </summary>
    [JsonConverter(typeof(ABConnectDateTimeConverter))]
    public DateTimeOffset? DateModifiedUtc { get; init; }

    /// <summary>The publication the document belongs to, when it was requested.</summary>
    public StandardPublication? Publication { get; init; }
}

/// <summary>
/// A publication, as embedded in a document or returned by a publication probe.
/// </summary>
public sealed record StandardPublication
{
    /// <summary>The AB Connect GUID of the publication.</summary>
    public string? Guid { get; init; }

    /// <summary>The publication's description, which is its title.</summary>
    [JsonPropertyName("descr")]
    public string? Description { get; init; }

    /// <summary>The publication's short acronym, for example <c>CCRS</c>.</summary>
    public string? Acronym { get; init; }

    /// <summary>The publication's code.</summary>
    public string? Code { get; init; }

    /// <summary>The JSON:API type of the embedded object, when AB Connect supplies one.</summary>
    public string? Type { get; init; }

    /// <summary>
    /// AB Connect's classification of the publication. An open string, not a closed enumeration;
    /// AB Connect publishes no complete value set.
    /// </summary>
    public string? PublicationType { get; init; }

    /// <summary>The publisher's URL for the publication.</summary>
    public string? SourceUrl { get; init; }

    /// <summary>
    /// The regions the publication applies to. <see langword="null"/> means the array was absent
    /// from the response; an empty list means it was present and empty.
    /// </summary>
    public IReadOnlyList<Region>? Regions { get; init; }

    /// <summary>
    /// The authorities that own the publication. <see langword="null"/> means the array was absent
    /// from the response; an empty list means it was present and empty.
    /// </summary>
    public IReadOnlyList<Authority>? Authorities { get; init; }
}

/// <summary>A geographic or jurisdictional region.</summary>
/// <param name="Guid">The AB Connect GUID of the region.</param>
/// <param name="Type">The region's classification. An open string, not a closed enumeration.</param>
/// <param name="Code">The region's code, for example a state postal abbreviation.</param>
/// <param name="Description">The region's description.</param>
public sealed record Region(
    string? Guid,
    string? Type,
    string? Code,
    [property: JsonPropertyName("descr")] string? Description);

/// <summary>An authority that owns one or more publications.</summary>
/// <param name="Guid">The AB Connect GUID of the authority.</param>
/// <param name="Acronym">The authority's short acronym.</param>
/// <param name="Description">The authority's description, which is its name.</param>
public sealed record Authority(
    string? Guid,
    string? Acronym,
    [property: JsonPropertyName("descr")] string? Description);

/// <summary>
/// The thin publication shape that appears inside a facet's <c>details</c> array.
/// </summary>
/// <remarks>
/// Distinct from <see cref="StandardPublication"/> on purpose. A facet response never carries
/// regions, authorities, or a publication type, so giving the facet shape its own type prevents a
/// caller from reaching for a property that will always be null there.
/// </remarks>
/// <param name="Guid">The AB Connect GUID of the publication.</param>
/// <param name="Type">The publication's type as reported in the facet detail.</param>
/// <param name="Code">The publication's code.</param>
/// <param name="Acronym">The publication's short acronym.</param>
/// <param name="SourceUrl">The publisher's URL for the publication.</param>
/// <param name="Description">The publication's description, which is its title.</param>
public sealed record Publication(
    string? Guid,
    string? Type,
    string? Code,
    string? Acronym,
    string? SourceUrl,
    [property: JsonPropertyName("descr")] string? Description);

/// <summary>
/// The thin document shape that appears inside a facet's <c>details</c> array: description, adopt
/// year, and GUID, and nothing else.
/// </summary>
/// <remarks>
/// Distinct from <see cref="StandardDocument"/> on purpose; see the remarks on
/// <see cref="Publication"/>. To obtain a fully populated document, probe for it.
/// </remarks>
/// <param name="Guid">The AB Connect GUID of the document.</param>
/// <param name="AdoptYear">The year the document was adopted.</param>
/// <param name="Description">The document's description, which is its title.</param>
public sealed record DocumentSummary(
    string? Guid,
    string? AdoptYear,
    [property: JsonPropertyName("descr")] string? Description);

/// <summary>
/// The thin section shape that appears inside a facet's <c>details</c> array.
/// </summary>
/// <remarks>Distinct from <see cref="StandardSection"/> on purpose; see the remarks on <see cref="Publication"/>.</remarks>
/// <param name="Guid">The AB Connect GUID of the section.</param>
/// <param name="Description">The section's description.</param>
public sealed record SectionSummary(
    string? Guid,
    [property: JsonPropertyName("descr")] string? Description);

/// <summary>
/// A standard's position in its document hierarchy.
/// </summary>
/// <remarks>
/// The JSON:API <c>data</c> wrapper AB Connect nests each reference in is flattened at the
/// deserialization layer, so a caller writes <c>standard.Relationships?.Parent?.Id</c> rather than
/// walking an extra level.
/// </remarks>
public sealed record StandardRelationships
{
    /// <summary>
    /// The parent standard, or <see langword="null"/> for a root standard or an unrequested field.
    /// </summary>
    /// <remarks>
    /// An empty wrapper, <c>{"data":{}}</c>, is how AB Connect reports a to-one relationship that
    /// names nothing; it reads as a reference whose members are both null rather than as a null
    /// reference. See <see cref="ABRelationshipRefConverter"/>.
    /// </remarks>
    [JsonConverter(typeof(ABRelationshipRefConverter))]
    public ABRelationshipRef? Parent { get; init; }

    /// <summary>
    /// The child standards. <see langword="null"/> means the array was absent from the response,
    /// which happens when <c>children</c> was not requested or the account is not licensed for it;
    /// an empty list means the standard is a leaf.
    /// </summary>
    [JsonConverter(typeof(ABRelationshipRefListConverter))]
    public IReadOnlyList<ABRelationshipRef>? Children { get; init; }
}

/// <summary>A JSON:API resource identifier, flattened out of its <c>data</c> wrapper.</summary>
/// <param name="Id">The referenced resource's id, which for a standard is its AB Connect GUID.</param>
/// <param name="Type">The referenced resource's JSON:API type.</param>
public sealed record ABRelationshipRef(string? Id, string? Type);
