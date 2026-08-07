using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnCourse.ABConnect.Models;

/// <summary>
/// Reads an AB Connect "year" value that may arrive as either a JSON string or a JSON number into a
/// <see cref="string"/>.
/// </summary>
/// <remarks>
/// <para>
/// AB Connect is inconsistent about the year fields (adopt, revision, implementation, assessment,
/// obsolete): on a standard's embedded document they come back as strings, but inside a facet's
/// <c>details[].data</c> the same fields come back as bare JSON numbers. A model that types them as
/// <see cref="string"/> therefore fails to deserialize a numeric facet value with a type-mismatch at,
/// for example, <c>$.adopt_year</c>. This converter accepts both shapes and normalizes to the string
/// form the rest of the model expects, so a year is never silently dropped and never forces the
/// caller to know which representation a given endpoint used.
/// </para>
/// <para>
/// Applied by attribute to each year member rather than through
/// <see cref="JsonSerializerOptions.Converters"/>, so the source-generated metadata carries it and a
/// caller supplying their own options cannot accidentally drop it. JSON <c>null</c> and an empty
/// string read as <see langword="null"/>.
/// </para>
/// </remarks>
public sealed class ABConnectYearConverter : JsonConverter<string?>
{
    /// <summary>Whether this converter is asked to handle JSON <c>null</c> itself. It is.</summary>
    public override bool HandleNull => true;

    /// <summary>Reads a year value that may be a string or a number.</summary>
    /// <param name="reader">The reader positioned on the value.</param>
    /// <param name="typeToConvert">The type being converted.</param>
    /// <param name="options">The serializer options in force.</param>
    /// <returns>The year as a string, or <see langword="null"/> for JSON null or an empty string.</returns>
    /// <exception cref="JsonException">The value is neither a string, a number, nor null.</exception>
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                string? text = reader.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();

            case JsonTokenType.Number:
                // AB Connect sends years as integers in facet details; keep the lossless integer form
                // and fall back to the raw decimal text for anything that is not a whole number.
                return reader.TryGetInt64(out long year)
                    ? year.ToString(CultureInfo.InvariantCulture)
                    : reader.GetDecimal().ToString(CultureInfo.InvariantCulture);

            default:
                throw new JsonException(
                    $"Expected an AB Connect year as a JSON string or number but found {reader.TokenType}.");
        }
    }

    /// <summary>Writes a year value as a JSON string.</summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="value">The year, or <see langword="null"/> to write JSON null.</param>
    /// <param name="options">The serializer options in force.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is null.</exception>
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}
