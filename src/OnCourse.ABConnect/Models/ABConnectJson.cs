using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace OnCourse.ABConnect.Models;

/// <summary>
/// The serializer options every AB Connect response is read with.
/// </summary>
/// <remarks>
/// <para>
/// There is one instance so that there is one answer. The SDK's own root types resolve through the
/// source-generated <see cref="ABConnectJsonSerializerContext"/>, the internal transport shapes
/// through <see cref="ABConnectWireJsonSerializerContext"/>, and anything else through reflection.
/// The reflection resolver is not a convenience: <c>IABConnectClient.GetFacetAsync</c> is generic over
/// a facet detail type the caller chooses, which no source generator running inside this assembly can
/// have seen.
/// </para>
/// <para>
/// <see cref="JsonSerializerOptions.PropertyNamingPolicy"/> is set for that reflection path only. The
/// source-generated metadata already has snake-case names baked in from its own
/// <c>JsonSourceGenerationOptions</c>; setting the policy here makes a caller-supplied facet type
/// behave the same way rather than silently failing to bind <c>source_url</c> or <c>adopt_year</c>.
/// </para>
/// </remarks>
public static class ABConnectJson
{
    /// <summary>
    /// The canonical options instance, read-only and safe to share across threads.
    /// </summary>
    /// <remarks>
    /// Registration hands this to the client, and the response mapper deserializes with it. A caller
    /// that substitutes its own options must chain both serializer contexts, or the internal envelope
    /// shapes will not resolve at run time.
    /// </remarks>
    public static JsonSerializerOptions Default { get; } = CreateDefault();

    /// <summary>Builds the canonical options and freezes them.</summary>
    private static JsonSerializerOptions CreateDefault()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            TypeInfoResolver = JsonTypeInfoResolver.Combine(
                ABConnectJsonSerializerContext.Default,
                ABConnectWireJsonSerializerContext.Default,
                new DefaultJsonTypeInfoResolver()),
        };

        options.MakeReadOnly();
        return options;
    }
}
