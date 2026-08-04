using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnCourse.ABConnect.Models;

/// <summary>
/// One change event, as the JSON:API resource object AB Connect returns from the events feed.
/// </summary>
/// <remarks>
/// Compare two events by <see cref="Id"/> or by <see cref="ABEventAttributes.Seq"/>, not with
/// <c>==</c>. Record equality is synthesized member by member, and two members defeat it:
/// <see cref="ABEventAttributes.AffectedProperties"/> is a collection and so compares by reference,
/// and <see cref="AffectedProperty.NewValue"/> and <see cref="AffectedProperty.PreviousValue"/> are
/// <see cref="JsonElement"/> values, whose equality is not structural either. Both are consequences of
/// modelling AB Connect's variant-typed payload honestly rather than pretending to know its shape.
/// </remarks>
public sealed record ABEvent
{
    /// <summary>
    /// The JSON:API resource id of the event. A lowercase UUID, unlike AB Connect's uppercase
    /// standard GUIDs, so never compare the two forms without normalizing case.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>The JSON:API resource type, normally <c>events</c>.</summary>
    public string? Type { get; init; }

    /// <summary>The event's attributes, or <see langword="null"/> when the requested field set asked for none.</summary>
    public ABEventAttributes? Attributes { get; init; }

    /// <summary>
    /// The standards the event refers to, or <see langword="null"/> when no relationship field was
    /// requested or the account is not licensed for any of them.
    /// </summary>
    public ABEventRelationships? Relationships { get; init; }

    /// <summary>
    /// Whether this is a deliverability event, meaning a document or section was added to or removed
    /// from the account's license.
    /// </summary>
    /// <remarks>
    /// AB Connect draws the line explicitly: "Unlike Standard change Events, deliverability Events
    /// should be acted upon immediately to ensure your system is in line with license agreements."
    /// The helper is conservative by construction. Anything it does not recognize, including any
    /// target or change type AB Connect adds in future, is not a delivery event, so an unknown
    /// combination falls into the slower, review-gated path rather than being applied unreviewed.
    /// </remarks>
    public bool IsDeliveryEvent =>
        Attributes?.Target is ABEventTargets.Document or ABEventTargets.Section
        && Attributes.ChangeType is ABChangeTypes.Added or ABChangeTypes.Removed;
}

/// <summary>The attribute block of an <see cref="ABEvent"/>.</summary>
public sealed record ABEventAttributes
{
    /// <summary>
    /// The event's position in AB Connect's global, monotonically increasing sequence. This is the
    /// watermark a delta pull persists and re-anchors on. Typed <see cref="long"/> rather than
    /// <see cref="int"/> because the counter spans all AB Connect partners and has no documented
    /// ceiling.
    /// </summary>
    public required long Seq { get; init; }

    /// <summary>
    /// When the event occurred, in UTC. Parsed from AB Connect's <c>yyyy-MM-dd HH:mm:ss</c> form by
    /// <see cref="ABConnectDateTimeConverter"/>. AB Connect states that dates were "problematic due
    /// to concurrency and race conditions" as a feed cursor, so use <see cref="Seq"/> as the
    /// watermark and treat this as informational.
    /// </summary>
    [JsonConverter(typeof(ABConnectDateTimeConverter))]
    public DateTimeOffset? DateUtc { get; init; }

    /// <summary>
    /// What kind of change occurred. Compare against <see cref="ABChangeTypes"/>; this is an open
    /// string, and live sampling has observed values AB Connect does not document.
    /// </summary>
    public string? ChangeType { get; init; }

    /// <summary>
    /// What kind of thing changed. Compare against <see cref="ABEventTargets"/>; this is an open
    /// string, not a closed enumeration.
    /// </summary>
    public string? Target { get; init; }

    /// <summary>The AB Connect GUID of the thing that changed, interpreted according to <see cref="Target"/>.</summary>
    public string? Guid { get; init; }

    /// <summary>The AB Connect GUID of the document the change falls under.</summary>
    public string? DocumentGuid { get; init; }

    /// <summary>The AB Connect GUID of the section the change falls under.</summary>
    public string? SectionGuid { get; init; }

    /// <summary>
    /// The individual properties this event changed. <see langword="null"/> means the array was
    /// absent from the response; an empty list means it was present and empty.
    /// </summary>
    public IReadOnlyList<AffectedProperty>? AffectedProperties { get; init; }
}

/// <summary>
/// One property changed by an event, with its previous and new values.
/// </summary>
/// <remarks>
/// <c>new_value</c> and <c>previous_value</c> are variant types in AB Connect and are exposed as raw
/// JSON. AB Connect states that they "are of variant types" and publishes no schema for them, so the
/// SDK does not pretend to know the shape. Live examples include
/// <c>{"name":"section.seq","previous_value":22,"new_value":19}</c> and
/// <c>{"name":"number.raw","previous_value":"a.","new_value":"MU:CT.HSAdv.Pr4.a"}</c>. The JSON key
/// for the property name is <c>name</c>, not a case transform of <c>Property</c>.
/// </remarks>
/// <param name="Property">The dotted path of the property that changed, for example <c>section.seq</c>.</param>
/// <param name="NewValue">The value after the change, as raw JSON of whatever type AB Connect sent.</param>
/// <param name="PreviousValue">The value before the change, as raw JSON of whatever type AB Connect sent.</param>
public sealed record AffectedProperty(
    [property: JsonPropertyName("name")] string? Property,
    JsonElement? NewValue,
    JsonElement? PreviousValue);

/// <summary>
/// The standards an event refers to. Each is modeled as a single optional reference because AB
/// Connect documents neither their cardinality nor their precise meaning.
/// </summary>
public sealed record ABEventRelationships
{
    /// <summary>
    /// The standard the event concerns. Observed populated on live data, including on deletion
    /// events, where it carries the standard being deleted.
    /// </summary>
    [JsonConverter(typeof(ABRelationshipRefConverter))]
    public ABRelationshipRef? Standard { get; init; }

    /// <summary>The standard that became non-deliverable. Observed populated on live data.</summary>
    [JsonConverter(typeof(ABRelationshipRefConverter))]
    public ABRelationshipRef? NondeliverableStandard { get; init; }

    /// <summary>
    /// The standard that was deleted. AB Connect documents this relationship, but live sampling of
    /// roughly 2,100 events found it never populated on any deletion or removal event; the standard
    /// being deleted was referenced through <see cref="Standard"/> instead. It is retained because
    /// the vendor documents it and a different partner or a future change could populate it, but a
    /// soft-delete design must not assume it is currently reachable.
    /// </summary>
    /// <remarks>
    /// The observed wire form is the empty wrapper <c>{"data":{}}</c>, which reads as a reference
    /// whose <see cref="ABRelationshipRef.Id"/> and <see cref="ABRelationshipRef.Type"/> are both
    /// null. Test for a usable reference with <c>DeletedStandard?.Id is not null</c>, never with
    /// <c>DeletedStandard is not null</c>.
    /// </remarks>
    [JsonConverter(typeof(ABRelationshipRefConverter))]
    public ABRelationshipRef? DeletedStandard { get; init; }
}
