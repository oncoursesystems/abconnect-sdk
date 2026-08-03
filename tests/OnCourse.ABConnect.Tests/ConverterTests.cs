using System.Text.Json;
using OnCourse.ABConnect.Models;
using Xunit;

namespace OnCourse.ABConnect.Tests;

/// <summary>
/// Covers the two custom converters in both directions and on every malformed input they are asked to
/// judge. DeserializationTests reads the recorded fixtures; this group is the converters' own
/// contract: what they refuse, what they coerce, and what they write back.
/// </summary>
/// <remarks>
/// The writing direction matters even though the SDK never sends a standard to AB Connect. A consumer
/// caching a snapshot serializes these models and must get the same absent-versus-empty distinction
/// back on the way in, and a converter that can read a shape it cannot write is a converter that
/// silently loses information on the round trip.
/// </remarks>
public sealed class ConverterTests
{
    private static readonly JsonSerializerOptions Options = ABConnectJson.Default;

    /// <summary>
    /// The three readings of a to-one relationship, which are three different answers and must never
    /// be collapsed: JSON null and a bare object mean the relationship was absent; an empty
    /// <c>data</c> object means it was returned and names nothing, which is gate G-4's observed form
    /// for <c>deleted_standard</c>; and a populated <c>data</c> object is a reference.
    /// </summary>
    [Fact]
    public void AToOneRelationshipReadsAbsentAndNamesNothingDifferently()
    {
        Assert.Null(ReadParent("null"));
        Assert.Null(ReadParent("{}"));
        Assert.Null(ReadParent("""{"data":null}"""));

        ABRelationshipRef namesNothing = Assert.IsType<ABRelationshipRef>(ReadParent("""{"data":{}}"""));
        Assert.Null(namesNothing.Id);
        Assert.Null(namesNothing.Type);

        ABRelationshipRef reference = Assert.IsType<ABRelationshipRef>(
            ReadParent("""{"data":{"id":"P1","type":"standards"}}"""));
        Assert.Equal("P1", reference.Id);
        Assert.Equal("standards", reference.Type);
    }

    /// <summary>
    /// A bare resource identifier with no <c>data</c> wrapper is accepted, because a server that
    /// flattens the wrapper itself is still naming a resource. An object naming none of the three
    /// members reads as absent.
    /// </summary>
    [Fact]
    public void AToOneRelationshipAcceptsAnUnwrappedIdentifier()
    {
        ABRelationshipRef reference = Assert.IsType<ABRelationshipRef>(
            ReadParent("""{"id":"P1","type":"standards"}"""));

        Assert.Equal("P1", reference.Id);
        Assert.Null(ReadParent("""{"meta":{"licensed":true}}"""));
    }

    /// <summary>
    /// Unknown members inside a relationship are skipped rather than refused, including nested ones,
    /// so a server adding a <c>links</c> or <c>meta</c> block to a relationship does not break a
    /// read.
    /// </summary>
    [Fact]
    public void UnknownMembersInsideARelationshipAreSkipped()
    {
        ABRelationshipRef reference = Assert.IsType<ABRelationshipRef>(ReadParent(
            """{"links":{"self":"https://example.invalid/x"},"data":{"id":"P1","type":"standards","meta":{"a":[1,2]}}}"""));

        Assert.Equal("P1", reference.Id);
        Assert.Equal("standards", reference.Type);
    }

    /// <summary>
    /// A JSON:API identifier is a string by specification, but a numeric one is read as its literal
    /// text rather than refused, because coercing keeps a deviating server usable while dropping the
    /// reference would silently lose a standard.
    /// </summary>
    [Theory]
    [InlineData("""{"data":{"id":42,"type":"standards"}}""", "42")]
    [InlineData("""{"data":{"id":9007199254740993,"type":"standards"}}""", "9007199254740993")]
    [InlineData("""{"data":{"id":1.5,"type":"standards"}}""", "1.5")]
    public void ANumericIdentifierIsReadAsItsLiteralText(string json, string expected)
    {
        ABRelationshipRef reference = Assert.IsType<ABRelationshipRef>(ReadParent(json));

        Assert.Equal(expected, reference.Id);
    }

    [Fact]
    public void AnIdentifierMemberThatIsExplicitlyNullReadsAsNull()
    {
        ABRelationshipRef reference = Assert.IsType<ABRelationshipRef>(
            ReadParent("""{"data":{"id":null,"type":"standards"}}"""));

        Assert.Null(reference.Id);
        Assert.Equal("standards", reference.Type);
    }

    /// <summary>
    /// Every shape a to-one relationship cannot be is refused rather than read as absent, because
    /// silently reading a malformed relationship as "no parent" would rebuild a hierarchy wrongly.
    /// </summary>
    [Theory]
    [InlineData("7")]
    [InlineData("\"standards\"")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"data":7}""")]
    [InlineData("""{"data":{"id":true}}""")]
    [InlineData("""{"data":{"type":[1]}}""")]
    public void AMalformedToOneRelationshipIsRefused(string json)
        => Assert.Throws<JsonException>(() => ReadParent(json));

    /// <summary>
    /// The to-many reading, where absence and emptiness are the distinction section 7.1 exists to
    /// preserve: null means the array was not returned, which happens when the field was not requested
    /// or the account is not licensed for it, and an empty list means the standard is a leaf.
    /// </summary>
    [Fact]
    public void AToManyRelationshipReadsAbsentAndEmptyDifferently()
    {
        Assert.Null(ReadChildren("null"));
        Assert.Null(ReadChildren("{}"));
        Assert.Null(ReadChildren("""{"data":null}"""));

        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ABRelationshipRef>>(ReadChildren("""{"data":[]}""")));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ABRelationshipRef>>(ReadChildren("[]")));

        IReadOnlyList<ABRelationshipRef> children = Assert.IsAssignableFrom<IReadOnlyList<ABRelationshipRef>>(
            ReadChildren("""{"data":[{"id":"C1","type":"standards"},{"id":"C2","type":"standards"}]}"""));

        Assert.Equal(2, children.Count);
        Assert.Equal("C1", children[0].Id);
        Assert.Equal("C2", children[1].Id);
    }

    /// <summary>
    /// A to-many <c>data</c> member that arrived as a single object rather than an array is read as a
    /// one-element list, and an empty single object as an empty list, so a server that collapses a
    /// one-element array does not cost the caller a child.
    /// </summary>
    [Fact]
    public void AToManyRelationshipAcceptsASingleObjectUnderData()
    {
        IReadOnlyList<ABRelationshipRef> single = Assert.IsAssignableFrom<IReadOnlyList<ABRelationshipRef>>(
            ReadChildren("""{"data":{"id":"C1","type":"standards"}}"""));

        Assert.Equal("C1", Assert.Single(single).Id);

        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ABRelationshipRef>>(
            ReadChildren("""{"data":{}}""")));
    }

    /// <summary>
    /// A null element inside the array is skipped rather than turned into a reference naming nothing,
    /// because a null in a to-many list is noise rather than a resource.
    /// </summary>
    [Fact]
    public void ANullElementInsideAToManyArrayIsSkipped()
    {
        IReadOnlyList<ABRelationshipRef> children = Assert.IsAssignableFrom<IReadOnlyList<ABRelationshipRef>>(
            ReadChildren("""{"data":[{"id":"C1","type":"standards"},null,{"id":"C2","type":"standards"}]}"""));

        Assert.Equal(2, children.Count);
    }

    /// <summary>An element that is an empty object is a reference naming nothing, not a dropped row.</summary>
    [Fact]
    public void AnEmptyElementInsideAToManyArrayIsAReferenceNamingNothing()
    {
        IReadOnlyList<ABRelationshipRef> children = Assert.IsAssignableFrom<IReadOnlyList<ABRelationshipRef>>(
            ReadChildren("""{"data":[{}]}"""));

        ABRelationshipRef only = Assert.Single(children);
        Assert.Null(only.Id);
        Assert.Null(only.Type);
    }

    [Fact]
    public void UnknownMembersBesideAToManyDataArrayAreSkipped()
    {
        IReadOnlyList<ABRelationshipRef> children = Assert.IsAssignableFrom<IReadOnlyList<ABRelationshipRef>>(
            ReadChildren("""{"links":{"self":"https://example.invalid/x"},"data":[{"id":"C1"}]}"""));

        Assert.Equal("C1", Assert.Single(children).Id);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("\"standards\"")]
    [InlineData("false")]
    [InlineData("""{"data":7}""")]
    [InlineData("""{"data":"C1"}""")]
    [InlineData("""{"data":[7]}""")]
    [InlineData("""{"data":["C1"]}""")]
    public void AMalformedToManyRelationshipIsRefused(string json)
        => Assert.Throws<JsonException>(() => ReadChildren(json));

    /// <summary>
    /// Writing puts both relationship forms back inside the <c>data</c> wrapper they were read out of,
    /// and null writes as JSON null, so a consumer caching a snapshot reads back what it wrote.
    /// </summary>
    [Fact]
    public void RelationshipsRoundTripThroughTheWrappedWireForm()
    {
        StandardRelationships original = new()
        {
            Parent = new ABRelationshipRef("P1", "standards"),
            Children = [new ABRelationshipRef("C1", "standards"), new ABRelationshipRef(null, null)],
        };

        string json = JsonSerializer.Serialize(original, Options);

        Assert.Contains("""{"data":{"id":"P1","type":"standards"}}""", json, StringComparison.Ordinal);
        Assert.Contains("""{"data":[{"id":"C1","type":"standards"},{}]}""", json, StringComparison.Ordinal);

        StandardRelationships? read = JsonSerializer.Deserialize<StandardRelationships>(json, Options);

        Assert.Equal("P1", read?.Parent?.Id);
        Assert.Equal(2, read?.Children?.Count);
        Assert.Null(read?.Children?[1].Id);
    }

    /// <summary>
    /// The absent-versus-empty distinction survives a write as well as a read, which is what makes a
    /// cached snapshot trustworthy: an unrequested relationship stays unrequested and a leaf stays a
    /// leaf.
    /// </summary>
    [Fact]
    public void AbsentAndEmptyRelationshipsRoundTripAsThemselves()
    {
        string absent = JsonSerializer.Serialize(new StandardRelationships(), Options);
        StandardRelationships? readAbsent = JsonSerializer.Deserialize<StandardRelationships>(absent, Options);
        Assert.Null(readAbsent?.Parent);
        Assert.Null(readAbsent?.Children);

        string leaf = JsonSerializer.Serialize(new StandardRelationships { Children = [] }, Options);
        StandardRelationships? readLeaf = JsonSerializer.Deserialize<StandardRelationships>(leaf, Options);
        Assert.NotNull(readLeaf?.Children);
        Assert.Empty(readLeaf.Children);
    }

    [Fact]
    public void ARelationshipConverterRefusesToWriteToANullWriter()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ABRelationshipRefConverter().Write(null!, new ABRelationshipRef("A", "standards"), Options));

        Assert.Throws<ArgumentNullException>(
            () => new ABRelationshipRefListConverter().Write(null!, [], Options));
    }

    /// <summary>
    /// AB Connect's own date format is a space-separated UTC instant with no zone designator. It is
    /// read as UTC, and ISO-8601 is accepted alongside it because AB Connect's documentation shows
    /// both.
    /// </summary>
    [Theory]
    [InlineData("2017-12-06 13:20:29", "2017-12-06T13:20:29+00:00")]
    [InlineData("2020-02-29 00:00:00", "2020-02-29T00:00:00+00:00")]
    [InlineData("2017-12-06T13:20:29Z", "2017-12-06T13:20:29+00:00")]
    [InlineData("2017-12-06T15:20:29+02:00", "2017-12-06T13:20:29+00:00")]
    [InlineData("2017-12-06T13:20:29", "2017-12-06T13:20:29+00:00")]
    public void ADateIsReadAsUtcInEveryAcceptedForm(string wire, string expected)
    {
        DateTimeOffset? read = ReadDate($"\"{wire}\"");

        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), read);
        Assert.Equal(TimeSpan.Zero, read?.Offset);
    }

    /// <summary>
    /// The three forms of "no date": JSON null, the empty string, and whitespace. AB Connect uses all
    /// three for a standard that has not been deleted.
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void AnAbsentDateReadsAsNull(string json)
        => Assert.Null(ReadDate(json));

    /// <summary>
    /// An unparseable date throws rather than reading as null, because a null date means "not
    /// deleted" and quietly inventing that from garbage would hide a deletion.
    /// </summary>
    [Theory]
    [InlineData("\"not a date\"")]
    [InlineData("\"2017-13-45 99:99:99\"")]
    [InlineData("1512570029")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void AnUnreadableDateIsRefused(string json)
        => Assert.Throws<JsonException>(() => ReadDate(json));

    /// <summary>Dates are written back in AB Connect's own format, normalized to UTC.</summary>
    [Fact]
    public void ADateIsWrittenInABConnectsFormatNormalizedToUtc()
    {
        Assert.Equal(
            "\"2017-12-06 13:20:29\"",
            WriteDate(new DateTimeOffset(2017, 12, 6, 13, 20, 29, TimeSpan.Zero)));

        Assert.Equal(
            "\"2017-12-06 13:20:29\"",
            WriteDate(new DateTimeOffset(2017, 12, 6, 15, 20, 29, TimeSpan.FromHours(2))));

        Assert.Equal("null", WriteDate(null));
    }

    [Fact]
    public void ADateWrittenInABConnectsFormatReadsBackAsTheSameInstant()
    {
        DateTimeOffset original = new(2024, 7, 4, 23, 59, 58, TimeSpan.Zero);

        Assert.Equal(original, ReadDate(WriteDate(original)));
    }

    [Fact]
    public void TheDateConverterRefusesToWriteToANullWriter()
        => Assert.Throws<ArgumentNullException>(
            () => new ABConnectDateTimeConverter().Write(null!, DateTimeOffset.UnixEpoch, Options));

    private static ABRelationshipRef? ReadParent(string relationshipJson)
    {
        StandardRelationships? relationships = JsonSerializer.Deserialize<StandardRelationships>(
            $$"""{"parent":{{relationshipJson}}}""",
            Options);

        return relationships?.Parent;
    }

    private static IReadOnlyList<ABRelationshipRef>? ReadChildren(string relationshipJson)
    {
        StandardRelationships? relationships = JsonSerializer.Deserialize<StandardRelationships>(
            $$"""{"children":{{relationshipJson}}}""",
            Options);

        return relationships?.Children;
    }

    private static DateTimeOffset? ReadDate(string valueJson)
    {
        StandardAttributes? attributes = JsonSerializer.Deserialize<StandardAttributes>(
            $$"""{"date_deleted_utc":{{valueJson}}}""",
            Options);

        return attributes?.DateDeletedUtc;
    }

    private static string WriteDate(DateTimeOffset? value)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            new ABConnectDateTimeConverter().Write(writer, value, Options);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
