using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnCourse.ABConnect.Models;

/// <summary>
/// The wire shape of a facet response: facet data arrives under <c>meta.facets</c>, not under
/// <c>data</c>.
/// </summary>
/// <remarks>
/// <para>
/// A facet request is a standards request with <c>limit=0</c>, so its <c>data</c> array is empty or
/// absent and everything of interest is in <c>meta</c>. The shape is
/// <c>{"meta":{"count":n,"took":n,"facets":[{"facet":"document","count":n,"details":[{"count":n,"data":{...}}]}]}}</c>.
/// </para>
/// <para>
/// This type is internal on purpose. It mirrors a transport detail, and
/// <see cref="ABFacet{TValue}"/> is the shape a consumer sees. Deserializing it requires a resolver
/// that knows it, which is why <see cref="ABConnectJson.Default"/> chains
/// <see cref="ABConnectWireJsonSerializerContext"/>.
/// </para>
/// </remarks>
internal sealed record ABFacetEnvelope
{
    /// <summary>The response's <c>meta</c> block, or null when the body carried none.</summary>
    public ABFacetEnvelopeMeta? Meta { get; init; }
}

/// <summary>The <c>meta</c> block of a facet response.</summary>
internal sealed record ABFacetEnvelopeMeta
{
    /// <summary>The number of standards the facet was computed over, not the number of facet values.</summary>
    public int Count { get; init; }

    /// <summary>How long AB Connect reports it spent on the query, in milliseconds.</summary>
    public int Took { get; init; }

    /// <summary>
    /// The requested facets. <see langword="null"/> means the array was absent from the response; an
    /// empty list means it was present and empty. The distinction is preserved here for the same
    /// reason it is preserved on the model: absence and emptiness are different answers.
    /// </summary>
    public IReadOnlyList<ABFacetBlock>? Facets { get; init; }
}

/// <summary>One facet inside <c>meta.facets</c>.</summary>
/// <remarks>
/// The member naming the facet is <c>facet</c> on the payloads this SDK's predecessor consumed
/// successfully in production. <c>facet_type</c> is accepted as an alias because the AB Connect
/// documentation and the version 3 design both refer to the member by that name, and one of the two
/// spellings being wrong must not silently produce an empty facet.
/// </remarks>
internal sealed record ABFacetBlock
{
    /// <summary>The facet's name as AB Connect reported it, under the member <c>facet</c>.</summary>
    [JsonPropertyName("facet")]
    public string? Facet { get; init; }

    /// <summary>The facet's name under the alias member <c>facet_type</c>.</summary>
    [JsonPropertyName("facet_type")]
    public string? FacetType { get; init; }

    /// <summary>
    /// The number of distinct values AB Connect reports for this facet. It exceeds the number of
    /// returned <see cref="Details"/> when the facet was truncated at AB Connect's 10,000-value
    /// ceiling.
    /// </summary>
    public int Count { get; init; }

    /// <summary>
    /// The facet's values. <see langword="null"/> means the array was absent from the response; an
    /// empty list means it was present and empty.
    /// </summary>
    public IReadOnlyList<ABFacetDetail>? Details { get; init; }

    /// <summary>The facet's name from whichever member carried it.</summary>
    [JsonIgnore]
    public string? Name => Facet ?? FacetType;
}

/// <summary>One value of a facet: a count and the detail object it counts.</summary>
internal sealed record ABFacetDetail
{
    /// <summary>The number of matched standards carrying this facet value.</summary>
    public int Count { get; init; }

    /// <summary>
    /// The detail object, held as raw JSON because its shape depends on which facet was requested and,
    /// through <c>FacetQuery&lt;TValue&gt;</c>, on a type the caller chooses.
    /// </summary>
    public JsonElement? Data { get; init; }
}

/// <summary>
/// Projects the <c>meta.facets</c> wire shape onto <see cref="ABFacet{TValue}"/>.
/// </summary>
internal static class ABFacetProjection
{
    /// <summary>
    /// Projects one facet out of a facet response.
    /// </summary>
    /// <typeparam name="TValue">The detail type each facet value deserializes into.</typeparam>
    /// <param name="envelope">The deserialized response body.</param>
    /// <param name="facetName">The facet name that was requested, used to select the right block.</param>
    /// <param name="serializerOptions">
    /// The options each detail object is deserialized with. For a caller-chosen
    /// <typeparamref name="TValue"/> these must resolve that type, which is why
    /// <see cref="ABConnectJson.Default"/> chains a reflection-based resolver behind the
    /// source-generated ones.
    /// </param>
    /// <returns>
    /// The facet, never null. A response carrying no matching facet projects to a facet with a
    /// reported count of zero and no values, which is the honest reading: AB Connect answered
    /// successfully and the facet has no values.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The value-or-throw guarantee applies to whatever calls this: the method either returns a non-null value that
    /// was deserialized from a 2xx response body, or it throws. It never returns null, and it never
    /// returns a default-constructed envelope.
    /// </para>
    /// <para>
    /// A detail object that will not deserialize into <typeparamref name="TValue"/> raises
    /// <see cref="JsonException"/> rather than being skipped, because a silently dropped facet value
    /// is indistinguishable from a facet value AB Connect never sent. The caller must translate that
    /// into <c>ABConnectResponseFormatException</c>, exactly as it does for a body that will not
    /// deserialize at all.
    /// </para>
    /// <para>
    /// The block is selected by name, case-insensitively. When no block matches but exactly one was
    /// returned, that one is used: a facet request asks for a single facet, so a name AB Connect
    /// echoes back with different punctuation must not turn a populated response into an empty one.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="facetName"/> is null or whitespace.</exception>
    /// <exception cref="JsonException">A facet detail object could not be read as <typeparamref name="TValue"/>.</exception>
    internal static ABFacet<TValue> ToFacet<TValue>(
        this ABFacetEnvelope? envelope,
        string facetName,
        JsonSerializerOptions serializerOptions)
        where TValue : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(facetName);
        ArgumentNullException.ThrowIfNull(serializerOptions);

        ABFacetBlock? block = SelectBlock(envelope?.Meta?.Facets, facetName);

        if (block is null)
        {
            return new ABFacet<TValue>
            {
                FacetName = facetName,
                ReportedCount = 0,
                Values = [],
            };
        }

        List<ABFacetValue<TValue>> values = [];

        foreach (ABFacetDetail detail in block.Details ?? [])
        {
            if (detail.Data is not { } data
                || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            TValue value = data.Deserialize<TValue>(serializerOptions)
                ?? throw new JsonException(
                    $"A value of the AB Connect facet '{facetName}' deserialized to null as "
                    + $"{typeof(TValue).Name}.");

            values.Add(new ABFacetValue<TValue>(value, detail.Count));
        }

        return new ABFacet<TValue>
        {
            FacetName = block.Name ?? facetName,
            ReportedCount = block.Count,
            Values = values,
        };
    }

    /// <summary>
    /// Finds the block for the requested facet: an exact name match first, then the only block there
    /// is, then nothing.
    /// </summary>
    private static ABFacetBlock? SelectBlock(IReadOnlyList<ABFacetBlock>? facets, string facetName)
    {
        if (facets is null || facets.Count == 0)
        {
            return null;
        }

        foreach (ABFacetBlock candidate in facets)
        {
            if (string.Equals(candidate.Name, facetName, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return facets.Count == 1 ? facets[0] : null;
    }
}
