using System.Text.Json.Serialization;

namespace OnCourse.ABConnect.Models;

/// <summary>
/// The source-generated <see cref="JsonSerializerContext"/> the SDK deserializes every AB Connect
/// response through.
/// </summary>
/// <remarks>
/// <para>
/// Snake-case naming is applied at the context level, which removes the per-property attribute that
/// would otherwise be needed on <c>date_modified_utc</c>, <c>prefix_enhanced</c>, <c>change_type</c>,
/// <c>document_guid</c>, and every other multi-word AB Connect field. Only <c>descr</c> still needs
/// an explicit name, because it is an abbreviation rather than a case transform. Unmapped members
/// are skipped so that a field AB Connect adds does not fail a deserialization.
/// </para>
/// <para>
/// The two custom converters are applied by attribute on the members that need them rather than
/// through <see cref="System.Text.Json.JsonSerializerOptions.Converters"/>, so the generated metadata
/// carries them and no caller can lose them by supplying their own options.
/// </para>
/// <para>
/// Every root type the SDK deserializes is registered here or, for the internal transport shapes, on
/// <see cref="ABConnectWireJsonSerializerContext"/>. <see cref="ABConnectJson.Default"/> chains both
/// plus a reflection-based resolver, and is the options instance the SDK is meant to run on.
/// </para>
/// <para>
/// One AB Connect body is deliberately absent from both contexts: the JSON:API <c>errors[]</c>
/// document. It is not deserialized through the serializer at all.
/// <c>ABConnectResponseMapper.ParseErrors</c> reads it with <see cref="System.Text.Json.JsonDocument"/>
/// and hand-projects it onto <c>ABConnectApiError</c>, which is both trim-safe and, more importantly,
/// total: an error body arrives precisely when something has already gone wrong, so the parse must
/// never itself throw. It returns an empty list for a body that is not JSON, that carries no
/// <c>errors</c> member, or whose <c>errors</c> member is not an array, and it tolerates a
/// <c>status</c> that arrives as a number rather than a string. A registered DTO would turn any of
/// those into a second failure layered on the first and lose the status mapping the caller needs.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(ABPage<Standard>))]
[JsonSerializable(typeof(ABPage<ABEvent>))]
[JsonSerializable(typeof(Standard))]
[JsonSerializable(typeof(StandardAttributes))]
[JsonSerializable(typeof(StandardRelationships))]
[JsonSerializable(typeof(StandardDocument))]
[JsonSerializable(typeof(StandardPublication))]
[JsonSerializable(typeof(ABEvent))]
[JsonSerializable(typeof(ABEventAttributes))]
[JsonSerializable(typeof(ABEventRelationships))]
[JsonSerializable(typeof(PageMeta))]
[JsonSerializable(typeof(PageLinks))]
[JsonSerializable(typeof(Region))]
[JsonSerializable(typeof(Authority))]
[JsonSerializable(typeof(Publication))]
[JsonSerializable(typeof(DocumentSummary))]
[JsonSerializable(typeof(SectionSummary))]
public sealed partial class ABConnectJsonSerializerContext : JsonSerializerContext
{
}
