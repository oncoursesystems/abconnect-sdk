using System.Text.Json;
using OnCourse.ABConnect.Models;
using Xunit;

namespace OnCourse.ABConnect.Tests;

/// <summary>
/// Direct tests of the internal wire shapes that stand between a response body and the public
/// envelopes: <see cref="ABPageDocument{TResource}"/>, <see cref="ABResourceDocument"/>, and the
/// facet projection. These are where Invariant E1 is actually decided, so they are asserted directly
/// rather than only through <see cref="ABConnectClient"/>. A projection returning null is the signal
/// the client turns into <c>ABConnectResponseFormatException</c>; a projection returning an empty but
/// well-formed page is a successful answer that happens to have no rows. Confusing the two is defect
/// 2.1, the whole reason this release exists.
/// </summary>
public sealed class WireShapeTests
{
    private static readonly JsonSerializerOptions Options = ABConnectJson.Default;

    /// <summary>
    /// The <c>limit=0</c> body, verbatim in the shape gate G-1 observed: AB Connect omits both
    /// <c>data</c> and <c>links</c>. It is a successful response and must project to a page whose
    /// data is empty, not to null.
    /// </summary>
    [Fact]
    public void AMetaOnlyBodyProjectsToAnEmptyPageRatherThanToNull()
    {
        ABPageDocument<Standard> document = Deserialize<ABPageDocument<Standard>>(
            """{"meta":{"count":1471199,"offset":0,"took":181,"limit":0}}""");

        ABPage<Standard>? page = document.ToPageOrNull(dataRequired: false);

        ABPage<Standard> projected = Assert.IsType<ABPage<Standard>>(page);
        Assert.Empty(projected.Data);
        Assert.Equal(1471199, projected.Meta.Count);
        Assert.Equal(0, projected.Meta.Limit);
        Assert.NotNull(projected.Links);
        Assert.Null(projected.Links.Self);
        Assert.Null(projected.Links.Next);
    }

    /// <summary>
    /// A body with no <c>meta</c> block is not a list response at all. The projection refuses it
    /// rather than inventing counts, and the null it returns is what the client turns into a format
    /// exception.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"data":[],"links":{"self":"https://example.invalid/standards"}}""")]
    [InlineData("""{"meta":null}""")]
    public void ABodyWithNoMetaBlockProjectsToNull(string body)
    {
        ABPageDocument<Standard> document = Deserialize<ABPageDocument<Standard>>(body);

        Assert.Null(document.ToPageOrNull(dataRequired: true));
        Assert.Null(document.ToPageOrNull(dataRequired: false));
    }

    /// <summary>
    /// The counterpart to the meta-only case above, and the reason the projection takes a flag at all.
    /// A request that asked for rows and came back with counters but no <c>data</c> key is not an answer
    /// of "no rows"; it is a body the SDK cannot read. Collapsing it to an empty page would let the
    /// events walk read it as the end of the feed and report a clean run that processed nothing, which
    /// is defect 2.1 wearing a new costume.
    /// </summary>
    [Fact]
    public void AMetaOnlyBodyIsRefusedWhenTheRequestAskedForRows()
    {
        ABPageDocument<ABEvent> document = Deserialize<ABPageDocument<ABEvent>>(
            """{"meta":{"count":500,"offset":0,"took":12,"limit":100}}""");

        Assert.Null(document.ToPageOrNull(dataRequired: true));
    }

    /// <summary>
    /// The section 7.1 absent-versus-empty convention survives the projection in the one direction
    /// that matters: an absent array becomes an empty page, and an array that was present and empty
    /// also becomes an empty page, because by the time a consumer holds an
    /// <see cref="ABPage{T}"/> the question it needs answered is "how many rows", and the meta block
    /// carries the rest.
    /// </summary>
    [Fact]
    public void AnEmptyDataArrayWithAMetaBlockIsASuccessfulEmptyPage()
    {
        ABPageDocument<Standard> document = Deserialize<ABPageDocument<Standard>>(
            """{"data":[],"meta":{"count":0,"offset":0,"took":3,"limit":100}}""");

        ABPage<Standard> page = Assert.IsType<ABPage<Standard>>(document.ToPageOrNull(dataRequired: true));

        Assert.Empty(page.Data);
        Assert.Equal(0, page.Meta.Count);
    }

    /// <summary>
    /// AB Connect documents the lookup's behavior without publishing whether <c>data</c> is the
    /// resource object or a one-element array, so both are accepted. A shape the SDK has not observed
    /// live must not turn a successful lookup into a format error.
    /// </summary>
    [Fact]
    public void ALookupUnwrapsDataAsAnObject()
    {
        ABResourceDocument document = Deserialize<ABResourceDocument>(
            """{"data":{"id":"A","type":"standards","attributes":{"guid":"A","seq":7}}}""");

        Standard standard = Assert.IsType<Standard>(document.Unwrap<Standard>(Options));

        Assert.Equal("A", standard.Attributes?.Guid);
        Assert.Equal(7, standard.Attributes?.Seq);
    }

    [Fact]
    public void ALookupUnwrapsDataAsAOneElementArray()
    {
        ABResourceDocument document = Deserialize<ABResourceDocument>(
            """{"data":[{"id":"A","type":"standards","attributes":{"guid":"A","seq":7}}]}""");

        Standard standard = Assert.IsType<Standard>(document.Unwrap<Standard>(Options));

        Assert.Equal("A", standard.Attributes?.Guid);
    }

    /// <summary>
    /// Every shape that names no resource unwraps to null, which the client turns into a format
    /// exception rather than returning an empty standard. A JSON scalar under <c>data</c> is included
    /// because a coercion there would fabricate a resource out of a number.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"data":null}""")]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"data":7}""")]
    [InlineData("""{"data":"standards"}""")]
    [InlineData("""{"data":true}""")]
    public void ALookupThatNamesNoResourceUnwrapsToNull(string body)
    {
        ABResourceDocument document = Deserialize<ABResourceDocument>(body);

        Assert.Null(document.Unwrap<Standard>(Options));
    }

    [Fact]
    public void UnwrappingWithoutSerializerOptionsIsRejected()
    {
        ABResourceDocument document = Deserialize<ABResourceDocument>("""{"data":{}}""");

        Assert.Throws<ArgumentNullException>(() => document.Unwrap<Standard>(null!));
    }

    /// <summary>
    /// A lookup body whose resource will not deserialize raises <see cref="JsonException"/> rather
    /// than unwrapping to null, so the client reports a format failure instead of a missing standard.
    /// </summary>
    [Fact]
    public void ALookupWhoseResourceWillNotDeserializeThrows()
    {
        ABResourceDocument document = Deserialize<ABResourceDocument>(
            """{"data":{"id":"A","type":"standards","attributes":{"seq":"not a number"}}}""");

        Assert.Throws<JsonException>(() => document.Unwrap<Standard>(Options));
    }

    /// <summary>
    /// The facet name has two spellings in the wild. The block is read from whichever member carried
    /// it, because one of the two being wrong must not silently produce an empty facet.
    /// </summary>
    [Theory]
    [InlineData("facet")]
    [InlineData("facet_type")]
    public void AFacetBlockIsReadUnderEitherSpellingOfItsNameMember(string member)
    {
        ABFacetEnvelope envelope = Deserialize<ABFacetEnvelope>(
            """{"meta":{"count":118,"took":9,"facets":[{"NAME":"document.publication","count":2,"details":[{"count":90,"data":{"guid":"P1","descr":"First"}},{"count":28,"data":{"guid":"P2","descr":"Second"}}]}]}}"""
                .Replace("NAME", member, StringComparison.Ordinal));

        ABFacet<FacetValueProbe> facet = envelope.ToFacet<FacetValueProbe>("document.publication", Options);

        Assert.Equal("document.publication", facet.FacetName);
        Assert.Equal(2, facet.ReportedCount);
        Assert.Equal(2, facet.Values.Count);
        Assert.Equal("P1", facet.Values[0].Value.Guid);
        Assert.Equal(90, facet.Values[0].Count);
        Assert.False(facet.IsTruncated);
    }

    /// <summary>
    /// AB Connect truncates a facet at its ten-thousand-value ceiling while still reporting the true
    /// distinct count, so a caller enumerating values must be able to tell that it did not get all of
    /// them.
    /// </summary>
    [Fact]
    public void AFacetReportingMoreValuesThanItReturnedIsTruncated()
    {
        ABFacetEnvelope envelope = Deserialize<ABFacetEnvelope>(
            """
            {"meta":{"count":10842,"took":410,"facets":[{"facet_type":"section","count":10842,
            "details":[{"count":3,"data":{"guid":"S1"}},{"count":1,"data":{"guid":"S2"}}]}]}}
            """);

        ABFacet<FacetValueProbe> facet = envelope.ToFacet<FacetValueProbe>("section", Options);

        Assert.Equal(10842, facet.ReportedCount);
        Assert.Equal(2, facet.Values.Count);
        Assert.True(facet.IsTruncated);
    }

    /// <summary>
    /// Every shape carrying no matching facet projects to a successful facet with no values and the
    /// requested name echoed back, never to null and never to a throw. AB Connect answered; the facet
    /// simply has nothing in it.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"meta":null}""")]
    [InlineData("""{"meta":{"count":0,"took":1}}""")]
    [InlineData("""{"meta":{"count":0,"took":1,"facets":null}}""")]
    [InlineData("""{"meta":{"count":0,"took":1,"facets":[]}}""")]
    public void ABodyWithNoMatchingFacetProjectsToAnEmptyFacet(string body)
    {
        ABFacetEnvelope envelope = Deserialize<ABFacetEnvelope>(body);

        ABFacet<FacetValueProbe> facet = envelope.ToFacet<FacetValueProbe>("document", Options);

        Assert.Equal("document", facet.FacetName);
        Assert.Equal(0, facet.ReportedCount);
        Assert.Empty(facet.Values);
        Assert.False(facet.IsTruncated);
    }

    /// <summary>A null envelope, which is what a JSON <c>null</c> body deserializes to, is answered the same way.</summary>
    [Fact]
    public void ANullEnvelopeProjectsToAnEmptyFacet()
    {
        ABFacet<FacetValueProbe> facet = ((ABFacetEnvelope?)null).ToFacet<FacetValueProbe>("document", Options);

        Assert.Equal("document", facet.FacetName);
        Assert.Empty(facet.Values);
    }

    /// <summary>
    /// A facet request asks for exactly one facet, so a name AB Connect echoes back with different
    /// punctuation must not turn a populated response into an empty one. With more than one block and
    /// no name match there is nothing to guess from, and the empty reading is the honest one.
    /// </summary>
    [Fact]
    public void TheOnlyBlockReturnedIsUsedEvenWhenItsNameDoesNotMatch()
    {
        ABFacetEnvelope envelope = Deserialize<ABFacetEnvelope>(
            """
            {"meta":{"count":5,"took":2,"facets":[{"facet":"publication","count":1,
            "details":[{"count":5,"data":{"guid":"P1"}}]}]}}
            """);

        ABFacet<FacetValueProbe> facet = envelope.ToFacet<FacetValueProbe>("document.publication", Options);

        Assert.Single(facet.Values);
        Assert.Equal("publication", facet.FacetName);
    }

    [Fact]
    public void WithSeveralBlocksAndNoNameMatchTheFacetIsEmpty()
    {
        ABFacetEnvelope envelope = Deserialize<ABFacetEnvelope>(
            """
            {"meta":{"count":5,"took":2,"facets":[
              {"facet":"publication","count":1,"details":[{"count":5,"data":{"guid":"P1"}}]},
              {"facet":"section","count":1,"details":[{"count":5,"data":{"guid":"S1"}}]}]}}
            """);

        ABFacet<FacetValueProbe> facet = envelope.ToFacet<FacetValueProbe>("document", Options);

        Assert.Empty(facet.Values);
        Assert.Equal("document", facet.FacetName);
    }

    /// <summary>The block is matched case-insensitively, because a name is not data.</summary>
    [Fact]
    public void TheBlockNameIsMatchedCaseInsensitively()
    {
        ABFacetEnvelope envelope = Deserialize<ABFacetEnvelope>(
            """
            {"meta":{"count":5,"took":2,"facets":[
              {"facet":"DOCUMENT","count":1,"details":[{"count":5,"data":{"guid":"D1"}}]},
              {"facet":"section","count":1,"details":[{"count":5,"data":{"guid":"S1"}}]}]}}
            """);

        ABFacet<FacetValueProbe> facet = envelope.ToFacet<FacetValueProbe>("document", Options);

        Assert.Single(facet.Values);
        Assert.Equal("D1", facet.Values[0].Value.Guid);
    }

    /// <summary>
    /// A detail with no data object carries no value, so it is skipped rather than deserialized into
    /// nothing. A detail whose data will not deserialize throws instead, because a silently dropped
    /// facet value is indistinguishable from one AB Connect never sent.
    /// </summary>
    [Fact]
    public void ADetailWithNoDataIsSkippedWhileAnUnreadableOneThrows()
    {
        ABFacetEnvelope skippable = Deserialize<ABFacetEnvelope>(
            """
            {"meta":{"count":3,"took":1,"facets":[{"facet":"document","count":3,"details":[
              {"count":1},
              {"count":1,"data":null},
              {"count":1,"data":{"guid":"D1"}}]}]}}
            """);

        ABFacet<FacetValueProbe> facet = skippable.ToFacet<FacetValueProbe>("document", Options);
        Assert.Single(facet.Values);
        Assert.Equal("D1", facet.Values[0].Value.Guid);

        ABFacetEnvelope unreadable = Deserialize<ABFacetEnvelope>(
            """
            {"meta":{"count":1,"took":1,"facets":[{"facet":"document","count":1,"details":[
              {"count":1,"data":{"adopt_year":"not a number"}}]}]}}
            """);

        Assert.Throws<JsonException>(() => unreadable.ToFacet<FacetValueProbe>("document", Options));
    }

    /// <summary>A block with an absent details array is a facet with no values, not a failure.</summary>
    [Fact]
    public void ABlockWithNoDetailsArrayIsAFacetWithNoValues()
    {
        ABFacetEnvelope envelope = Deserialize<ABFacetEnvelope>(
            """{"meta":{"count":4,"took":1,"facets":[{"facet":"document","count":4}]}}""");

        ABFacet<FacetValueProbe> facet = envelope.ToFacet<FacetValueProbe>("document", Options);

        Assert.Empty(facet.Values);
        Assert.Equal(4, facet.ReportedCount);
        Assert.True(facet.IsTruncated);
    }

    /// <summary>
    /// AB Connect sends the document facet's <c>adopt_year</c> as a bare JSON number, even though the
    /// same field is a string on a standard's embedded document. The document facet value must still
    /// deserialize, with the year normalized to its string form, and a string or null year must keep
    /// working. This is the shape that failed live before the year converter existed.
    /// </summary>
    [Fact]
    public void ADocumentFacetValueAcceptsANumericStringOrNullAdoptYear()
    {
        const string json = """
        {"meta":{"count":3,"facets":[{"facet":"document","count":3,"details":[
          {"count":10,"data":{"guid":"G1","adopt_year":2016,"descr":"Doc One"}},
          {"count":5,"data":{"guid":"G2","adopt_year":"2020","descr":"Doc Two"}},
          {"count":1,"data":{"guid":"G3","adopt_year":null,"descr":"Doc Three"}}
        ]}]}}
        """;

        ABFacetEnvelope envelope = Deserialize<ABFacetEnvelope>(json);
        ABFacet<DocumentSummary> facet = envelope.ToFacet<DocumentSummary>("document", Options);

        Assert.Equal(3, facet.Values.Count);
        Assert.Equal("2016", facet.Values[0].Value.AdoptYear);   // numeric on the wire
        Assert.Equal("G1", facet.Values[0].Value.Guid);
        Assert.Equal("Doc One", facet.Values[0].Value.Description);
        Assert.Equal("2020", facet.Values[1].Value.AdoptYear);   // string on the wire
        Assert.Null(facet.Values[2].Value.AdoptYear);            // null on the wire
    }

    /// <summary>
    /// A missing facet name is a caller mistake, not an empty facet. Null surfaces as
    /// <see cref="ArgumentNullException"/> and blank as <see cref="ArgumentException"/>, both of which
    /// are argument failures rather than a response the caller could act on.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ProjectingWithoutAFacetNameIsRejected(string? facetName)
    {
        ABFacetEnvelope envelope = Deserialize<ABFacetEnvelope>("{}");

        Assert.ThrowsAny<ArgumentException>(() => envelope.ToFacet<FacetValueProbe>(facetName!, Options));
    }

    [Fact]
    public void ProjectingWithoutSerializerOptionsIsRejected()
    {
        ABFacetEnvelope envelope = Deserialize<ABFacetEnvelope>("{}");

        Assert.Throws<ArgumentNullException>(() => envelope.ToFacet<FacetValueProbe>("document", null!));
    }

    private static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new InvalidOperationException($"The fixture '{json}' did not deserialize as {typeof(T).Name}.");

    /// <summary>
    /// A caller-defined facet value type no source generator in the SDK has ever seen, which is why
    /// <see cref="ABConnectJson.Default"/> chains a reflection resolver behind the generated ones.
    /// </summary>
    private sealed record FacetValueProbe(string? Guid, string? Descr, int? AdoptYear);
}
