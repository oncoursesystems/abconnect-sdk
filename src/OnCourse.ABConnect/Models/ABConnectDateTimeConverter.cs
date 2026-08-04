using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnCourse.ABConnect.Models;

/// <summary>
/// Reads AB Connect's date format into a <see cref="DateTimeOffset"/>.
/// </summary>
/// <remarks>
/// <para>
/// AB Connect emits <c>yyyy-MM-dd HH:mm:ss</c>, which <see cref="System.Text.Json"/> will not parse
/// on its own. The value is interpreted as UTC, because every field carrying it is named with a
/// <c>_utc</c> suffix. An ISO-8601 value is accepted as a fallback, and JSON <c>null</c> or an empty
/// string reads as <see langword="null"/>.
/// </para>
/// <para>
/// The converter is applied by attribute to every date member of the model
/// (<c>date_utc</c>, <c>date_modified_utc</c>, and <c>date_deleted_utc</c>) rather than through
/// <see cref="JsonSerializerOptions.Converters"/>, so that the source-generated metadata carries it
/// and a caller who supplies their own options cannot accidentally drop it.
/// </para>
/// <para>
/// A value that is neither AB Connect's format nor ISO-8601 is a <see cref="JsonException"/>, not a
/// silent null. Silently discarding an unparseable timestamp is how a stale local copy goes
/// unnoticed, which is the class of failure this SDK exists to remove.
/// </para>
/// </remarks>
public sealed class ABConnectDateTimeConverter : JsonConverter<DateTimeOffset?>
{
    /// <summary>AB Connect's documented date format, interpreted as UTC.</summary>
    public const string ABConnectFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>
    /// The space-separated forms accepted without a time zone designator, all read as UTC. The
    /// fractional-second and date-only variants are tolerated because AB Connect documents neither
    /// their presence nor their absence.
    /// </summary>
    private static readonly string[] SpaceSeparatedFormats =
    [
        ABConnectFormat,
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd",
    ];

    /// <summary>
    /// Whether this converter is asked to handle JSON <c>null</c> itself. It is, so that null and the
    /// empty string are answered in one place rather than half here and half in the serializer.
    /// </summary>
    public override bool HandleNull => true;

    /// <summary>Reads an AB Connect date value.</summary>
    /// <param name="reader">The reader positioned on the value.</param>
    /// <param name="typeToConvert">The type being converted.</param>
    /// <param name="options">The serializer options in force.</param>
    /// <returns>The parsed instant, or <see langword="null"/> for JSON null or an empty string.</returns>
    /// <exception cref="JsonException">
    /// The value is not a string, or is a string in neither AB Connect's <c>yyyy-MM-dd HH:mm:ss</c>
    /// form nor ISO-8601.
    /// </exception>
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                string? text = reader.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : Parse(text.Trim());

            default:
                throw new JsonException(
                    $"Expected an AB Connect date as a JSON string but found {reader.TokenType}.");
        }
    }

    /// <summary>Writes a date value in AB Connect's format.</summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="value">The instant to write, or <see langword="null"/> to write JSON null.</param>
    /// <param name="options">The serializer options in force.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is null.</exception>
    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.Value.ToUniversalTime().ToString(ABConnectFormat, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Parses one non-empty date string, trying AB Connect's own format first and ISO-8601 second. A
    /// value carrying no zone designator is UTC; a value carrying one is normalized to UTC, which
    /// preserves the instant either way.
    /// </summary>
    private static DateTimeOffset Parse(string text)
    {
        if (DateTime.TryParseExact(
                text,
                SpaceSeparatedFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime naive))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(naive, DateTimeKind.Unspecified), TimeSpan.Zero);
        }

        if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset iso))
        {
            return iso;
        }

        throw new JsonException(
            $"'{text}' is neither an AB Connect date ({ABConnectFormat}, read as UTC) nor an ISO-8601 date.");
    }
}
