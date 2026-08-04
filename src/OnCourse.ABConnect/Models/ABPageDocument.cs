using System.Text.Json;

namespace OnCourse.ABConnect.Models;

/// <summary>
/// The wire shape of an AB Connect list response, before it becomes an <see cref="ABPage{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every member is nullable here and required on <see cref="ABPage{T}"/>. That is the whole point of
/// the type. AB Connect omits <c>data</c> and <c>links</c> entirely from a <c>limit=0</c> response,
/// whose observed body is exactly
/// <c>{"meta":{"count":1471199,"offset":0,"took":181,"limit":0}}</c>, so deserializing such a body
/// straight into <see cref="ABPage{T}"/> fails on its required members even though AB Connect answered
/// correctly. Deserializing into this type and projecting keeps the required members on the public
/// envelope, where they are a guarantee to the consumer, without turning a legitimate response into a
/// format error.
/// </para>
/// <para>
/// The value-or-throw guarantee is preserved, not weakened, by this projection: for every method on
/// <c>IABConnectClient</c> and <c>IABConnectFeed</c>, the method either returns a non-null value that
/// was deserialized from a 2xx response body, or it throws. It never returns null, and it never
/// returns a default-constructed envelope. A body with no <c>meta</c> block is not a list response at
/// all, so <see cref="ToPageOrNull"/> refuses it rather than inventing counts, and the caller raises
/// <c>ABConnectResponseFormatException</c>.
/// </para>
/// </remarks>
/// <typeparam name="TResource">The resource type carried in <see cref="Data"/>.</typeparam>
internal sealed record ABPageDocument<TResource>
{
    /// <summary>
    /// The page's resources. <see langword="null"/> means the array was absent, which is what a
    /// <c>limit=0</c> request produces; an empty list means it was present and empty.
    /// </summary>
    public IReadOnlyList<TResource>? Data { get; init; }

    /// <summary>The response's counters, or null when the body carried no <c>meta</c> block.</summary>
    public PageMeta? Meta { get; init; }

    /// <summary>The response's navigation links, or null when the body carried no <c>links</c> block.</summary>
    public PageLinks? Links { get; init; }

    /// <summary>
    /// Projects this wire document onto the public envelope.
    /// </summary>
    /// <param name="dataRequired">
    /// <see langword="true"/> when the request asked for rows, that is, when its limit was greater
    /// than zero. An absent <c>data</c> array is then a malformed list response and is refused.
    /// <see langword="false"/> for a meta-only request, where AB Connect legitimately omits the array.
    /// </param>
    /// <returns>
    /// The page, or <see langword="null"/> when <see cref="Meta"/> is absent, or when
    /// <paramref name="dataRequired"/> is set and <see cref="Data"/> is absent. Either way the body
    /// was not the AB Connect list response the endpoint promises. The caller must translate a null
    /// result into <c>ABConnectResponseFormatException</c>; it must never be surfaced to a consumer,
    /// because the value-or-throw guarantee promises a consumer either a deserialized value or a throw.
    /// </returns>
    /// <remarks>
    /// <para>
    /// An absent <c>links</c> block becomes a links object whose every URL is null. That is not
    /// invented information: AB Connect omits the block from a meta-only response, and the SDK does
    /// not follow <c>links.next</c> in any case.
    /// </para>
    /// <para>
    /// An absent <c>data</c> array is judged by the requested limit rather than uniformly, and the
    /// distinction is load-bearing. A <c>limit=0</c> request answers with
    /// <c>{"meta":{"count":1471199,"offset":0,"took":181,"limit":0}}</c> and no <c>data</c> key at
    /// all, so a meta-only probe must project to an empty page. But a request that asked for a
    /// hundred rows and came back with counters and no array is not an answer of "no rows", it is a
    /// body this SDK cannot read, and collapsing it to an empty page would let the events walk read
    /// it as the end of the feed: a failure wearing the shape of a successful empty result, which is
    /// the whole defect this release exists to remove. The requested limit is used rather than the
    /// echoed <c>meta.limit</c> so the check does not depend on the service agreeing about what was
    /// asked.
    /// </para>
    /// </remarks>
    public ABPage<TResource>? ToPageOrNull(bool dataRequired)
        => Meta is null || (dataRequired && Data is null)
            ? null
            : new ABPage<TResource>
            {
                Data = Data ?? [],
                Meta = Meta,
                Links = Links ?? new PageLinks(null, null, null, null, null),
            };
}

/// <summary>
/// The wire shape of an AB Connect single-resource response, the body of a lookup by GUID.
/// </summary>
/// <remarks>
/// <c>data</c> is held as raw JSON rather than as a typed member because AB Connect documents the
/// lookup's behavior ("the system will respond with the standard regardless of whether it is deleted
/// or not") without publishing whether the member is the resource object or a one-element array.
/// <see cref="Unwrap{TResource}"/> accepts either, so a shape this SDK has not observed live cannot
/// turn a successful lookup into a format error.
/// </remarks>
internal sealed record ABResourceDocument
{
    /// <summary>The resource, as raw JSON. Absent, null, object, and array are all possible.</summary>
    public JsonElement? Data { get; init; }

    /// <summary>The response's counters, when AB Connect includes them on a single-resource body.</summary>
    public PageMeta? Meta { get; init; }

    /// <summary>
    /// Deserializes the wrapped resource.
    /// </summary>
    /// <typeparam name="TResource">The resource type to read.</typeparam>
    /// <param name="serializerOptions">The options to deserialize with.</param>
    /// <returns>
    /// The resource, or <see langword="null"/> when <c>data</c> was absent, JSON null, or an empty
    /// array. The caller must translate a null result into <c>ABConnectResponseFormatException</c>,
    /// because a lookup too either returns a non-null value that was deserialized from a 2xx
    /// response body, or it throws.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="serializerOptions"/> is null.</exception>
    /// <exception cref="JsonException">The resource could not be read as <typeparamref name="TResource"/>.</exception>
    public TResource? Unwrap<TResource>(JsonSerializerOptions serializerOptions)
        where TResource : class
    {
        ArgumentNullException.ThrowIfNull(serializerOptions);

        if (Data is not { } data)
        {
            return null;
        }

        return data.ValueKind switch
        {
            JsonValueKind.Object => data.Deserialize<TResource>(serializerOptions),
            JsonValueKind.Array => data.GetArrayLength() == 0
                ? null
                : data[0].Deserialize<TResource>(serializerOptions),
            _ => null,
        };
    }
}
