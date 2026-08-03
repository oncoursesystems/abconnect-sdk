using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnCourse.ABConnect.Models;

/// <summary>
/// Flattens AB Connect's JSON:API relationship envelope into an <see cref="ABRelationshipRef"/>.
/// </summary>
/// <remarks>
/// <para>
/// AB Connect nests each reference inside a <c>data</c> wrapper. This converter removes that level
/// during deserialization so that a caller writes <c>standard.Relationships?.Parent?.Id</c> instead
/// of walking the wrapper, and so that the model does not carry a type whose only purpose is to
/// mirror a transport detail.
/// </para>
/// <para>
/// An absent to-one relationship is the empty object <c>{"data":{}}</c> on live AB Connect data, never
/// JSON <c>null</c>: <c>deleted_standard</c> was observed live in exactly that form on every deletion
/// event sampled. That form reads as an <see cref="ABRelationshipRef"/> whose
/// <see cref="ABRelationshipRef.Id"/> and <see cref="ABRelationshipRef.Type"/> are both null, which
/// is functionally equivalent to a null reference for <c>?.Id</c>-style access and must never throw.
/// </para>
/// <para>
/// The exact readings are: JSON <c>null</c> reads as null; <c>{"data":null}</c> reads as null; a
/// wrapper whose <c>data</c> is an object reads as a reference, with null members where the object is
/// empty or the member is missing; an object carrying <c>id</c> or <c>type</c> directly, with no
/// <c>data</c> wrapper at all, reads as that reference; and an object carrying none of the three
/// reads as null.
/// </para>
/// </remarks>
public sealed class ABRelationshipRefConverter : JsonConverter<ABRelationshipRef?>
{
    /// <summary>
    /// The reading of a present-but-empty reference: the relationship was returned, and it names
    /// nothing.
    /// </summary>
    internal static ABRelationshipRef Empty { get; } = new(null, null);

    /// <summary>
    /// Whether this converter is asked to handle JSON <c>null</c> itself. It is, so that every form a
    /// relationship arrives in is answered in one place.
    /// </summary>
    public override bool HandleNull => true;

    /// <summary>Reads a wrapped relationship reference.</summary>
    /// <param name="reader">The reader positioned on the value.</param>
    /// <param name="typeToConvert">The type being converted.</param>
    /// <param name="options">The serializer options in force.</param>
    /// <returns>
    /// The flattened reference, or <see langword="null"/> when the relationship is JSON null, when its
    /// <c>data</c> member is JSON null, or when it is an object naming nothing at all. An empty
    /// <c>data</c> object yields a reference whose members are null rather than a null reference.
    /// </returns>
    /// <exception cref="JsonException">The value is neither an object nor JSON null.</exception>
    public override ABRelationshipRef? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(
                $"Expected a JSON:API relationship object but found {reader.TokenType}.");
        }

        return ReadObject(ref reader, unwrapData: true);
    }

    /// <summary>Writes a relationship reference back in AB Connect's wrapped form.</summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="value">The reference to write, or <see langword="null"/> to write JSON null.</param>
    /// <param name="options">The serializer options in force.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is null.</exception>
    public override void Write(Utf8JsonWriter writer, ABRelationshipRef? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("data");
        WriteIdentifier(writer, value);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes one bare JSON:API resource identifier, the object that lives inside a <c>data</c>
    /// wrapper.
    /// </summary>
    internal static void WriteIdentifier(Utf8JsonWriter writer, ABRelationshipRef value)
    {
        writer.WriteStartObject();

        if (value.Id is not null)
        {
            writer.WriteString("id", value.Id);
        }

        if (value.Type is not null)
        {
            writer.WriteString("type", value.Type);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Reads one object, either a relationship wrapper (<paramref name="unwrapData"/> true) or the
    /// bare resource identifier inside it.
    /// </summary>
    /// <param name="reader">The reader positioned on the object's <c>StartObject</c> token.</param>
    /// <param name="unwrapData">Whether a <c>data</c> member should be descended into.</param>
    /// <returns>
    /// The reference the object names, or null when it names nothing. A wrapper whose <c>data</c>
    /// member was present as an object always yields a non-null reference.
    /// </returns>
    internal static ABRelationshipRef? ReadObject(ref Utf8JsonReader reader, bool unwrapData)
    {
        string? id = null;
        string? type = null;
        bool sawData = false;
        ABRelationshipRef? wrapped = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException(
                    $"Expected a property name inside a JSON:API relationship but found {reader.TokenType}.");
            }

            string name = reader.GetString() ?? string.Empty;
            reader.Read();

            switch (name)
            {
                case "data" when unwrapData:
                    sawData = true;
                    wrapped = reader.TokenType switch
                    {
                        JsonTokenType.Null => null,
                        JsonTokenType.StartObject => ReadObject(ref reader, unwrapData: false) ?? Empty,
                        _ => throw new JsonException(
                            "Expected a JSON:API relationship 'data' member to be an object or null but found "
                            + $"{reader.TokenType}. A to-many relationship must use "
                            + $"{nameof(ABRelationshipRefListConverter)}."),
                    };
                    break;

                case "id":
                    id = ReadText(ref reader, name);
                    break;

                case "type":
                    type = ReadText(ref reader, name);
                    break;

                default:
                    reader.Skip();
                    break;
            }
        }

        if (sawData)
        {
            return wrapped;
        }

        return id is null && type is null ? null : new ABRelationshipRef(id, type);
    }

    /// <summary>
    /// Reads one identifier member as text. A JSON number is read as its literal text, because a
    /// JSON:API identifier is a string by specification and coercing rather than throwing keeps a
    /// deviating server usable.
    /// </summary>
    private static string? ReadText(ref Utf8JsonReader reader, string member)
        => reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out long number)
                ? number.ToString(CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            _ => throw new JsonException(
                $"Expected the JSON:API relationship member '{member}' to be a string but found {reader.TokenType}."),
        };
}

/// <summary>
/// Flattens AB Connect's JSON:API to-many relationship envelope into a list of
/// <see cref="ABRelationshipRef"/>.
/// </summary>
/// <remarks>
/// <para>
/// The wire form is <c>{"children":{"data":[{"id":"...","type":"standards"}, ...]}}</c>. This
/// converter removes the <c>data</c> level so a caller iterates
/// <c>standard.Relationships?.Children</c> directly.
/// </para>
/// <para>
/// This SDK's nullability convention is preserved exactly: <see langword="null"/> means the
/// array was absent from the response, which happens when the field was not requested or the account
/// is not licensed for it, and an empty list means it was present and empty. The two are never
/// collapsed, because AB Connect silently omits relationships the account is not licensed for while
/// still returning HTTP 200, and treating that as "no children" would reintroduce the ambiguity this
/// SDK exists to remove.
/// </para>
/// </remarks>
public sealed class ABRelationshipRefListConverter : JsonConverter<IReadOnlyList<ABRelationshipRef>?>
{
    /// <summary>
    /// Whether this converter is asked to handle JSON <c>null</c> itself. It is, so that absence and
    /// emptiness are decided in one place.
    /// </summary>
    public override bool HandleNull => true;

    /// <summary>Reads a wrapped list of relationship references.</summary>
    /// <param name="reader">The reader positioned on the value.</param>
    /// <param name="typeToConvert">The type being converted.</param>
    /// <param name="options">The serializer options in force.</param>
    /// <returns>
    /// The flattened references. <see langword="null"/> when the relationship is JSON null, when its
    /// <c>data</c> member is JSON null, or when the object carries no <c>data</c> member at all, all
    /// of which mean the array was absent. An empty list when <c>data</c> was present and empty, which
    /// means the standard is a leaf.
    /// </returns>
    /// <exception cref="JsonException">The value is neither an object, an array, nor JSON null.</exception>
    public override IReadOnlyList<ABRelationshipRef>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.StartArray:
                return ReadArray(ref reader);

            case JsonTokenType.StartObject:
                return ReadWrapper(ref reader);

            default:
                throw new JsonException(
                    $"Expected a JSON:API to-many relationship object but found {reader.TokenType}.");
        }
    }

    /// <summary>Writes a list of relationship references back in AB Connect's wrapped form.</summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="value">The references to write, or <see langword="null"/> to write JSON null.</param>
    /// <param name="options">The serializer options in force.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is null.</exception>
    public override void Write(
        Utf8JsonWriter writer,
        IReadOnlyList<ABRelationshipRef>? value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("data");
        writer.WriteStartArray();

        foreach (ABRelationshipRef reference in value)
        {
            ABRelationshipRefConverter.WriteIdentifier(writer, reference);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>Reads the wrapper object and returns whatever its <c>data</c> member holds.</summary>
    private static IReadOnlyList<ABRelationshipRef>? ReadWrapper(ref Utf8JsonReader reader)
    {
        IReadOnlyList<ABRelationshipRef>? references = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException(
                    $"Expected a property name inside a JSON:API relationship but found {reader.TokenType}.");
            }

            string name = reader.GetString() ?? string.Empty;
            reader.Read();

            if (name != "data")
            {
                reader.Skip();
                continue;
            }

            references = reader.TokenType switch
            {
                JsonTokenType.Null => null,
                JsonTokenType.StartArray => ReadArray(ref reader),
                JsonTokenType.StartObject => ReadObject(ref reader) is { } single ? [single] : [],
                _ => throw new JsonException(
                    "Expected a JSON:API to-many relationship 'data' member to be an array or null but found "
                    + $"{reader.TokenType}."),
            };
        }

        return references;
    }

    /// <summary>Reads the <c>data</c> array itself, one bare resource identifier per element.</summary>
    private static IReadOnlyList<ABRelationshipRef> ReadArray(ref Utf8JsonReader reader)
    {
        List<ABRelationshipRef> references = [];

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                break;
            }

            if (reader.TokenType == JsonTokenType.Null)
            {
                continue;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException(
                    $"Expected a JSON:API resource identifier object but found {reader.TokenType}.");
            }

            references.Add(ReadObject(ref reader) ?? ABRelationshipRefConverter.Empty);
        }

        return references;
    }

    /// <summary>Reads one bare resource identifier, tolerating a nested <c>data</c> wrapper.</summary>
    private static ABRelationshipRef? ReadObject(ref Utf8JsonReader reader)
        => ABRelationshipRefConverter.ReadObject(ref reader, unwrapData: true);
}
