using System.Text.Json.Serialization;

namespace OnCourse.ABConnect.Models;

/// <summary>
/// The source-generated <see cref="JsonSerializerContext"/> for the transport-only shapes: the list
/// and single-resource wrappers and the facet envelope.
/// </summary>
/// <remarks>
/// <para>
/// These shapes are internal because they mirror AB Connect's JSON:API envelope rather than anything a
/// consumer should hold, so they cannot be registered on the public
/// <see cref="ABConnectJsonSerializerContext"/>: the generator would emit a public member with an
/// internal type. They get their own internal context instead, configured identically, and
/// <see cref="ABConnectJson.Default"/> chains the two.
/// </para>
/// <para>
/// Both contexts declare the same <see cref="JsonSourceGenerationOptionsAttribute"/> and the model's
/// converters are applied by attribute, so a type registered on both, such as
/// <see cref="Standard"/> reached through <see cref="ABPageDocument{TResource}"/>, deserializes
/// identically whichever resolver answers first.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(ABPageDocument<Standard>))]
[JsonSerializable(typeof(ABPageDocument<ABEvent>))]
[JsonSerializable(typeof(ABResourceDocument))]
[JsonSerializable(typeof(ABFacetEnvelope))]
internal sealed partial class ABConnectWireJsonSerializerContext : JsonSerializerContext
{
}
