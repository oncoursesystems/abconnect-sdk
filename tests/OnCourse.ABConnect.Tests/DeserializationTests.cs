using System.Runtime.CompilerServices;
using System.Text.Json;
using OnCourse.ABConnect.Http;
using OnCourse.ABConnect.Models;
using Xunit;

namespace OnCourse.ABConnect.Tests;

/// <summary>
/// The deserialization golden-file group from specification section 9. Every payload under
/// <c>Fixtures/</c> came either from AB Connect's own published documentation or from the live G-2,
/// G-4 and G-5 runs recorded in section 12, and each has a sibling <c>.source</c> file naming its
/// provenance.
/// </summary>
/// <remarks>
/// Locks down defects 9 through 12: the number variants, AB Connect's space-separated date form, the
/// absent-versus-empty collection convention of section 7.1, and open string enumerations. Records
/// are compared property by property and never with <c>Assert.Equal</c> on a whole model, because
/// record equality on these types compares collection members by reference and
/// <c>AffectedProperty</c> holds <see cref="JsonElement"/>, which has no structural equality at all.
/// </remarks>
public sealed class DeserializationTests
{
    /// <summary>
    /// The AB Connect document GUID used by gate G-3 and by most fixtures.
    /// </summary>
    private const string GateThreeDocumentGuid = "9D85340C-592E-11E6-A0F5-48E229C466BA";

    /// <summary>
    /// Every <c>change_type</c> value the gate G-4 run observed live on 2026-08-02, plus two values
    /// AB Connect has never sent. The gate's prose says eleven values and then lists twelve; the list
    /// is what was observed, so the list is what is tested. The point of the theory is that all of
    /// them, known and unknown alike, deserialize as opaque strings.
    /// </summary>
    public static TheoryData<string> ChangeTypeValues =>
    [
        "added",
        "removed",
        "deleted",
        "reordered",
        "moved",
        "updated spelling",
        "updated metadata",
        "updated capitalization",
        "updated labeling",
        "updated numbering",
        "updated wording",
        "updated punctuation",
        "a change type AB Connect has never sent",
        string.Empty,
    ];

    /// <summary>
    /// The three <c>target</c> values gate G-4 observed, plus one AB Connect has never sent.
    /// </summary>
    public static TheoryData<string> TargetValues => ["document", "section", "standard", "curriculum"];

    [Fact]
    public void StandardFullFieldsFixtureBindsEveryAttribute()
    {
        Standard standard = Deserialize<Standard>(ReadFixture("standard-full-fields.json"));

        Assert.Equal("1B2C3D4E-592E-11E6-A0F5-48E229C466BA", standard.Id);
        Assert.Equal("standards", standard.Type);

        StandardAttributes attributes = Assert.IsType<StandardAttributes>(standard.Attributes);
        Assert.Equal("1B2C3D4E-592E-11E6-A0F5-48E229C466BA", attributes.Guid);
        Assert.Equal(33, attributes.Seq);
        Assert.Equal(4, attributes.Level);
        Assert.Equal("Sub-objective", attributes.Label);
        Assert.Equal(ABStandardStatuses.Active, attributes.Status);
        Assert.Equal("objective", attributes.StandardType);

        StandardStatement statement = Assert.IsType<StandardStatement>(attributes.Statement);
        Assert.Equal("Analyze the role of personal finance in a consumer economy.", statement.Description);
        Assert.Equal(
            "Social Studies: Personal Finance. Analyze the role of personal finance in a consumer economy.",
            statement.CombinedDescription);

        StandardSection section = Assert.IsType<StandardSection>(attributes.Section);
        Assert.Equal("A41C7E90-592E-11E6-A0F5-48E229C466BA", section.Guid);
        Assert.Equal("Personal Finance", section.Description);

        StandardDocument document = Assert.IsType<StandardDocument>(attributes.Document);
        Assert.Equal(GateThreeDocumentGuid, document.Guid);
        Assert.Equal("College and Career Readiness Standards for Social Studies", document.Description);
        Assert.Equal("2016", document.AdoptYear);
        Assert.Null(document.RevisionYear);
        Assert.Equal("2017", document.ImplementationYear);
        Assert.Null(document.AssessmentYear);
        Assert.Null(document.ObsoleteYear);
        Assert.Equal("https://wvde.us/social-studies/", document.SourceUrl);

        StandardPublication publication = Assert.IsType<StandardPublication>(document.Publication);
        Assert.Equal("7C1D0A44-592E-11E6-A0F5-48E229C466BA", publication.Guid);
        Assert.Equal("CCRS", publication.Acronym);
        Assert.Equal("state standards", publication.PublicationType);
        Assert.Equal("https://wvde.us/", publication.SourceUrl);

        Assert.NotNull(publication.Regions);
        Region region = Assert.Single(publication.Regions);
        Assert.Equal("5B0F2E10-592E-11E6-A0F5-48E229C466BA", region.Guid);
        Assert.Equal("state", region.Type);
        Assert.Equal("WV", region.Code);
        Assert.Equal("West Virginia", region.Description);

        Assert.NotNull(publication.Authorities);
        Authority authority = Assert.Single(publication.Authorities);
        Assert.Equal("6A9E4C88-592E-11E6-A0F5-48E229C466BA", authority.Guid);
        Assert.Equal("WVDE", authority.Acronym);
        Assert.Equal("West Virginia Department of Education", authority.Description);
    }

    [Fact]
    public void AllFourNumberVariantsBindFromTheFixture()
    {
        Standard standard = Deserialize<Standard>(ReadFixture("standard-full-fields.json"));

        StandardNumber number = Assert.IsType<StandardNumber>(standard.Attributes?.Number);
        Assert.Equal("b.", number.Raw);
        Assert.Equal("WV.CCRS.SOC.9-12.PF.SS.C.33.b", number.Enhanced);
        Assert.Equal("WV.CCRS.SOC.9-12.PF.SS.C.33.b", number.PrefixEnhanced);
        Assert.Equal("WV.CCRS.SOC.9-12.PF.SS.C.33.b", number.RootEnhanced);
    }

    [Fact]
    public void AllFourNumberVariantsRoundTripThroughTheirWireNames()
    {
        StandardNumber original = new()
        {
            Raw = "b.",
            Enhanced = "WV.CCRS.SOC.9-12.PF.SS.C.33.b",
            PrefixEnhanced = "WV.CCRS.SOC.9-12.PF.SS.C.33.b",
            RootEnhanced = "WV.CCRS.SOC.9-12.PF.SS.C.33.b",
        };

        string json = JsonSerializer.Serialize(original, ABConnectJson.Default);

        // The wire names are AB Connect's, not a case transform of the C# member names. If the
        // serializer ever stopped applying snake case, a round trip would still pass while every live
        // payload silently bound to null, so the emitted names are asserted directly.
        Assert.Contains("\"raw\":", json, StringComparison.Ordinal);
        Assert.Contains("\"enhanced\":", json, StringComparison.Ordinal);
        Assert.Contains("\"prefix_enhanced\":", json, StringComparison.Ordinal);
        Assert.Contains("\"root_enhanced\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("alternate", json, StringComparison.Ordinal);

        StandardNumber roundTripped = Deserialize<StandardNumber>(json);
        Assert.Equal(original.Raw, roundTripped.Raw);
        Assert.Equal(original.Enhanced, roundTripped.Enhanced);
        Assert.Equal(original.PrefixEnhanced, roundTripped.PrefixEnhanced);
        Assert.Equal(original.RootEnhanced, roundTripped.RootEnhanced);
    }

    [Fact]
    public void ANumberBlockCarryingTheDroppedAlternateVariantStillDeserializes()
    {
        // number.alternate was removed from the model by the G-2 resolution because it was null on all
        // 750 sampled rows. AB Connect still emits the key, so an unmapped member must be skipped
        // rather than throwing: an added or resurrected vendor field must never break a pull.
        const string Json = """
            {
              "raw": "b.",
              "enhanced": "WV.CCRS.SOC.9-12.PF.SS.C.33.b",
              "prefix_enhanced": "WV.CCRS.SOC.9-12.PF.SS.C.33.b",
              "root_enhanced": "WV.CCRS.SOC.9-12.PF.SS.C.33.b",
              "alternate": "something AB Connect started sending"
            }
            """;

        StandardNumber number = Deserialize<StandardNumber>(Json);

        Assert.Equal("b.", number.Raw);
        Assert.Equal("WV.CCRS.SOC.9-12.PF.SS.C.33.b", number.RootEnhanced);
    }

    [Fact]
    public void ABConnectsSpaceSeparatedDateFormParsesAsUtc()
    {
        // The exact value named in specification section 9, from AB Connect's own delivery-event
        // example. System.Text.Json cannot read this form without ABConnectDateTimeConverter, which
        // is defect 10.
        const string Json = """
            {
              "id": "1e4dd3aa-c7ba-11e7-9d0a-0242ac110002",
              "type": "events",
              "attributes": {
                "seq": 1,
                "date_utc": "2017-11-12 00:00:00",
                "change_type": "added",
                "target": "document"
              }
            }
            """;

        ABEvent abEvent = Deserialize<ABEvent>(Json);

        DateTimeOffset dateUtc = Assert.IsType<DateTimeOffset>(abEvent.Attributes?.DateUtc);
        Assert.Equal(new DateTimeOffset(2017, 11, 12, 0, 0, 0, TimeSpan.Zero), dateUtc);
        Assert.Equal(TimeSpan.Zero, dateUtc.Offset);
        Assert.Equal(2017, dateUtc.Year);
        Assert.Equal(11, dateUtc.Month);
        Assert.Equal(12, dateUtc.Day);
        Assert.Equal(TimeSpan.Zero, dateUtc.TimeOfDay);
    }

    [Fact]
    public void ADateModifiedOnAStandardParsesAsUtcFromTheFixture()
    {
        Standard standard = Deserialize<Standard>(ReadFixture("standard-full-fields.json"));

        DateTimeOffset modified = Assert.IsType<DateTimeOffset>(standard.Attributes?.DateModifiedUtc);
        Assert.Equal(new DateTimeOffset(2016, 8, 5, 14, 22, 31, TimeSpan.Zero), modified);
        Assert.Equal(TimeSpan.Zero, modified.Offset);
    }

    [Fact]
    public void ANullDateDeletedYieldsNullRatherThanThrowing()
    {
        Standard standard = Deserialize<Standard>(ReadFixture("standard-full-fields.json"));

        Assert.NotNull(standard.Attributes);
        Assert.Null(standard.Attributes.DateDeletedUtc);
        Assert.NotNull(standard.Attributes.DateModifiedUtc);
    }

    [Fact]
    public void ADeletedStandardCarriesBothDatesAndTheDeletedStatus()
    {
        // The lookup fixture is the shape gate G-5's supplementary probe recorded: a direct
        // GET /standards/{guid} returns a deleted standard with no status filter needed.
        using JsonDocument document = JsonDocument.Parse(ReadFixture("standard-lookup.json"));
        Standard standard = Deserialize<Standard>(document.RootElement.GetProperty("data").GetRawText());

        Assert.NotNull(standard.Attributes);
        Assert.Equal(ABStandardStatuses.Deleted, standard.Attributes.Status);
        Assert.Equal(new DateTimeOffset(2021, 4, 19, 17, 41, 8, TimeSpan.Zero), standard.Attributes.DateDeletedUtc);
        Assert.Equal(new DateTimeOffset(2021, 4, 19, 17, 41, 8, TimeSpan.Zero), standard.Attributes.DateModifiedUtc);
    }

    [Fact]
    public void AnEmptyStringDateYieldsNullRatherThanThrowing()
    {
        const string Json = """
            {
              "id": "1B2C3D4E-592E-11E6-A0F5-48E229C466BA",
              "type": "standards",
              "attributes": { "guid": "1B2C3D4E-592E-11E6-A0F5-48E229C466BA", "date_modified_utc": "" }
            }
            """;

        Standard standard = Deserialize<Standard>(Json);

        Assert.Null(standard.Attributes?.DateModifiedUtc);
    }

    [Fact]
    public void AGarbageDateThrowsRatherThanSilentlyReadingAsNull()
    {
        // A date the SDK cannot read is a data problem the caller must hear about. Nulling it would
        // hide a standard's modification time, which is exactly what the incremental pull keys on.
        const string Json = """
            {
              "id": "1B2C3D4E-592E-11E6-A0F5-48E229C466BA",
              "type": "standards",
              "attributes": { "date_modified_utc": "the third of never" }
            }
            """;

        Assert.Throws<JsonException>(() => Deserialize<Standard>(Json));
    }

    [Fact]
    public void AnAbsentAffectedPropertiesArrayYieldsNullAndAnEmptyOneYieldsAnEmptyList()
    {
        // Section 7.1: null means AB Connect did not send the member, an empty list means it sent an
        // empty array. The distinction is load-bearing because AB Connect omits relationships the
        // account is not licensed for while still answering 200, so conflating the two would report
        // "no children" for a standard whose children were merely withheld.
        ABEvent absent = Deserialize<ABEvent>(ReadFixture("event-added-document.json"));
        Assert.NotNull(absent.Attributes);
        Assert.Null(absent.Attributes.AffectedProperties);

        ABEvent empty = Deserialize<ABEvent>(ReadFixture("event-deleted-standard.json"));
        Assert.NotNull(empty.Attributes);
        Assert.NotNull(empty.Attributes.AffectedProperties);
        Assert.Empty(empty.Attributes.AffectedProperties);
    }

    [Fact]
    public void AnAbsentGradesArrayYieldsNullAndAnEmptyOneYieldsAnEmptyList()
    {
        Standard withGrades = Deserialize<Standard>(ReadFixture("standard-full-fields.json"));
        Assert.NotNull(withGrades.Attributes?.EducationLevels?.Grades);
        Assert.Equal(4, withGrades.Attributes.EducationLevels.Grades.Count);

        using JsonDocument lookup = JsonDocument.Parse(ReadFixture("standard-lookup.json"));
        Standard withoutEducationLevels = Deserialize<Standard>(lookup.RootElement.GetProperty("data").GetRawText());
        Assert.Null(withoutEducationLevels.Attributes?.EducationLevels);

        const string EmptyGrades = """
            {
              "id": "1B2C3D4E-592E-11E6-A0F5-48E229C466BA",
              "type": "standards",
              "attributes": { "education_levels": { "grades": [] } }
            }
            """;
        Standard emptyGrades = Deserialize<Standard>(EmptyGrades);
        Assert.NotNull(emptyGrades.Attributes?.EducationLevels?.Grades);
        Assert.Empty(emptyGrades.Attributes.EducationLevels.Grades);
        Assert.Null(emptyGrades.Attributes.EducationLevels.Lowest);
        Assert.Null(emptyGrades.Attributes.EducationLevels.Highest);
    }

    [Fact]
    public void AnAbsentChildrenArrayYieldsNullAndAnEmptyOneYieldsAnEmptyList()
    {
        Standard withChildren = Deserialize<Standard>(ReadFixture("standard-full-fields.json"));
        Assert.NotNull(withChildren.Relationships?.Children);
        Assert.Equal(2, withChildren.Relationships.Children.Count);
        Assert.Equal("1B2C3D4F-592E-11E6-A0F5-48E229C466BA", withChildren.Relationships.Children[0].Id);
        Assert.Equal("standards", withChildren.Relationships.Children[0].Type);

        using JsonDocument lookup = JsonDocument.Parse(ReadFixture("standard-lookup.json"));
        Standard emptyChildren = Deserialize<Standard>(lookup.RootElement.GetProperty("data").GetRawText());
        Assert.NotNull(emptyChildren.Relationships?.Children);
        Assert.Empty(emptyChildren.Relationships.Children);

        const string NoRelationshipsBlock = """
            {
              "id": "1B2C3D4E-592E-11E6-A0F5-48E229C466BA",
              "type": "standards"
            }
            """;
        Standard noRelationships = Deserialize<Standard>(NoRelationshipsBlock);
        Assert.Null(noRelationships.Relationships);

        const string EmptyRelationshipsBlock = """
            {
              "id": "1B2C3D4E-592E-11E6-A0F5-48E229C466BA",
              "type": "standards",
              "relationships": {}
            }
            """;
        Standard emptyRelationships = Deserialize<Standard>(EmptyRelationshipsBlock);
        Assert.NotNull(emptyRelationships.Relationships);
        Assert.Null(emptyRelationships.Relationships.Parent);
        Assert.Null(emptyRelationships.Relationships.Children);
    }

    [Fact]
    public void GradesAreOrderedByTheDocumentedSequenceNotByArrayOrder()
    {
        // The fixture deliberately lists grades 11, 9, 12, 10. AB Connect documents seq as the
        // ordering key, so an implementation that trusts array order gets 11 and 10 here.
        Standard standard = Deserialize<Standard>(ReadFixture("standard-full-fields.json"));

        EducationLevels levels = Assert.IsType<EducationLevels>(standard.Attributes?.EducationLevels);
        Assert.Equal("09", levels.Lowest?.Code);
        Assert.Equal(9, levels.Lowest?.Seq);
        Assert.Equal("Ninth grade", levels.Lowest?.Description);
        Assert.Equal("12", levels.Highest?.Code);
        Assert.Equal(12, levels.Highest?.Seq);
        Assert.Equal("Twelfth grade", levels.Highest?.Description);
    }

    [Theory]
    [MemberData(nameof(ChangeTypeValues))]
    public void EveryObservedAndUnobservedChangeTypeSurvivesAsAString(string changeType)
    {
        string json = $$"""
            {
              "id": "3e81b2f7-47ac-4d10-9f0b-5c6ad2938e71",
              "type": "events",
              "attributes": {
                "seq": 702955,
                "date_utc": "2026-07-28 15:09:33",
                "change_type": {{JsonSerializer.Serialize(changeType)}},
                "target": "standard"
              }
            }
            """;

        ABEvent abEvent = Deserialize<ABEvent>(json);

        Assert.Equal(changeType, abEvent.Attributes?.ChangeType);
    }

    [Theory]
    [MemberData(nameof(TargetValues))]
    public void EveryObservedAndUnobservedTargetSurvivesAsAString(string target)
    {
        string json = $$"""
            {
              "id": "3e81b2f7-47ac-4d10-9f0b-5c6ad2938e71",
              "type": "events",
              "attributes": {
                "seq": 702955,
                "change_type": "added",
                "target": {{JsonSerializer.Serialize(target)}}
              }
            }
            """;

        ABEvent abEvent = Deserialize<ABEvent>(json);

        Assert.Equal(target, abEvent.Attributes?.Target);
    }

    [Fact]
    public void AnUnknownStatusAndPublicationTypeSurviveAsStrings()
    {
        const string Json = """
            {
              "id": "1B2C3D4E-592E-11E6-A0F5-48E229C466BA",
              "type": "standards",
              "attributes": {
                "status": "archived-in-some-future-release",
                "document": { "publication": { "publication_type": "a classification AB Connect invented" } }
              }
            }
            """;

        Standard standard = Deserialize<Standard>(Json);

        Assert.Equal("archived-in-some-future-release", standard.Attributes?.Status);
        Assert.Equal(
            "a classification AB Connect invented",
            standard.Attributes?.Document?.Publication?.PublicationType);
    }

    [Fact]
    public void AffectedPropertyBindsFromTheWireKeyNameAndKeepsVariantValues()
    {
        // Gate G-4 found the live key is "name", not a case transform of the C# member Property. Under
        // UnmappedMemberHandling.Skip a wrong name binds to null forever and no test that only checks
        // "does it deserialize" would notice, which is the bug class this whole release exists to kill.
        ABEvent reordered = Deserialize<ABEvent>(ReadFixture("event-reordered-section.json"));

        Assert.NotNull(reordered.Attributes?.AffectedProperties);
        AffectedProperty change = Assert.Single(reordered.Attributes.AffectedProperties);
        Assert.Equal("section.seq", change.Property);
        Assert.Equal(JsonValueKind.Number, change.PreviousValue?.ValueKind);
        Assert.Equal(22, change.PreviousValue?.GetInt32());
        Assert.Equal(JsonValueKind.Number, change.NewValue?.ValueKind);
        Assert.Equal(19, change.NewValue?.GetInt32());
    }

    [Fact]
    public void AffectedPropertyKeepsStringVariantValues()
    {
        ABEvent renumbered = Deserialize<ABEvent>(ReadFixture("event-updated-numbering-standard.json"));

        Assert.NotNull(renumbered.Attributes?.AffectedProperties);
        AffectedProperty change = Assert.Single(renumbered.Attributes.AffectedProperties);
        Assert.Equal("number.raw", change.Property);
        Assert.Equal(JsonValueKind.String, change.PreviousValue?.ValueKind);
        Assert.Equal("a.", change.PreviousValue?.GetString());
        Assert.Equal("MU:CT.HSAdv.Pr4.a", change.NewValue?.GetString());
    }

    [Fact]
    public void AffectedPropertiesCarryMixedValueKindsInOneArray()
    {
        ABEvent metadata = Deserialize<ABEvent>(ReadFixture("event-nondeliverable-standard.json"));

        Assert.NotNull(metadata.Attributes?.AffectedProperties);
        Assert.Equal(2, metadata.Attributes.AffectedProperties.Count);
        Assert.Equal("statement.descr", metadata.Attributes.AffectedProperties[0].Property);
        Assert.Equal(JsonValueKind.String, metadata.Attributes.AffectedProperties[0].NewValue?.ValueKind);
        Assert.Equal("level", metadata.Attributes.AffectedProperties[1].Property);
        Assert.Equal(3, metadata.Attributes.AffectedProperties[1].PreviousValue?.GetInt32());
        Assert.Equal(4, metadata.Attributes.AffectedProperties[1].NewValue?.GetInt32());
    }

    [Fact]
    public void AToOneRelationshipWhoseDataIsTheEmptyObjectYieldsARefWithNullMembers()
    {
        // Gate G-4: AB Connect sends {"data":{}} rather than JSON null for an unreferenced to-one
        // relationship, and deleted_standard was {} on every one of roughly 2,100 sampled events. The
        // reference must therefore be present with null members, never a throw.
        ABEvent deleted = Deserialize<ABEvent>(ReadFixture("event-deleted-standard.json"));

        ABEventRelationships relationships = Assert.IsType<ABEventRelationships>(deleted.Relationships);

        Assert.NotNull(relationships.Standard);
        Assert.Equal("26998816-1a35-11eb-bb5b-0e20088bb29a", relationships.Standard.Id);
        Assert.Equal("standards", relationships.Standard.Type);

        Assert.NotNull(relationships.NondeliverableStandard);
        Assert.Null(relationships.NondeliverableStandard.Id);
        Assert.Null(relationships.NondeliverableStandard.Type);

        Assert.NotNull(relationships.DeletedStandard);
        Assert.Null(relationships.DeletedStandard.Id);
        Assert.Null(relationships.DeletedStandard.Type);
    }

    [Fact]
    public void ARelationshipObjectWithNoDataMemberAtAllYieldsNull()
    {
        // The converter draws a documented distinction: a bare {} means the relationship object
        // carried no data member and reads as absent, while {"data":{}} means present and naming
        // nothing. This is the same absent-versus-empty rule as section 7.1, one level deeper.
        const string Json = """
            {
              "id": "1B2C3D4E-592E-11E6-A0F5-48E229C466BA",
              "type": "standards",
              "relationships": { "parent": {}, "children": {} }
            }
            """;

        Standard standard = Deserialize<Standard>(Json);

        Assert.NotNull(standard.Relationships);
        Assert.Null(standard.Relationships.Parent);
        Assert.Null(standard.Relationships.Children);
    }

    [Fact]
    public void AParentRelationshipFlattensOutOfItsDataWrapper()
    {
        Standard standard = Deserialize<Standard>(ReadFixture("standard-full-fields.json"));

        ABRelationshipRef parent = Assert.IsType<ABRelationshipRef>(standard.Relationships?.Parent);
        Assert.Equal("1B2C3D4D-592E-11E6-A0F5-48E229C466BA", parent.Id);
        Assert.Equal("standards", parent.Type);
    }

    [Fact]
    public void TheDeliveryEventHelperRecognizesOnlyDocumentAndSectionAddedOrRemoved()
    {
        ABEvent addedDocument = Deserialize<ABEvent>(ReadFixture("event-added-document.json"));
        Assert.True(addedDocument.IsDeliveryEvent);

        ABEvent reorderedSection = Deserialize<ABEvent>(ReadFixture("event-reordered-section.json"));
        Assert.False(reorderedSection.IsDeliveryEvent);

        ABEvent deletedStandard = Deserialize<ABEvent>(ReadFixture("event-deleted-standard.json"));
        Assert.False(deletedStandard.IsDeliveryEvent);
    }

    [Fact]
    public void TheEventsPageFixtureBindsItsLinksAndMetaBlockAndEveryRow()
    {
        ABPage<ABEvent> page = Deserialize<ABPage<ABEvent>>(ReadFixture("events-page.json"));

        Assert.Equal(100, page.Meta.Limit);
        Assert.Equal(0, page.Meta.Offset);
        Assert.Equal(5, page.Meta.Count);
        Assert.Equal(33, page.Meta.Took);

        Assert.NotNull(page.Links.Self);
        Assert.NotNull(page.Links.First);
        Assert.Null(page.Links.Prev);
        Assert.Null(page.Links.Next);
        Assert.NotNull(page.Links.Last);

        Assert.Equal(5, page.Data.Count);
        Assert.Equal([702955L, 703118L, 703204L, 703311L, 703486L], page.Data.Select(e => e.Attributes!.Seq));
        Assert.Equal(
            ["added", "reordered", "updated numbering", "deleted", "updated metadata"],
            page.Data.Select(e => e.Attributes!.ChangeType));
    }

    [Fact]
    public void TheStandardsPageFixtureBindsItsLinksAndMetaBlockAndEveryRow()
    {
        ABPage<Standard> page = Deserialize<ABPage<Standard>>(ReadFixture("standards-page.json"));

        Assert.Equal(2, page.Meta.Limit);
        Assert.Equal(0, page.Meta.Offset);
        Assert.Equal(1163, page.Meta.Count);
        Assert.Equal(27, page.Meta.Took);
        Assert.NotNull(page.Links.Next);
        Assert.Equal(2, page.Data.Count);
        Assert.All(page.Data, standard => Assert.NotNull(standard.Attributes?.Guid));
    }

    [Fact]
    public void TheMetaOnlyBodyCarriesCountersWithLimitZero()
    {
        // The observed limit=0 body, verbatim: it has a meta block and no data and no links at all.
        // A consumer sees this through ABConnectClient, which is asserted elsewhere; here the point is
        // that the counters themselves bind, including the count of every standard on the account.
        using JsonDocument document = JsonDocument.Parse(ReadFixture("standards-meta-only.json"));
        PageMeta meta = Deserialize<PageMeta>(document.RootElement.GetProperty("meta").GetRawText());

        Assert.Equal(0, meta.Limit);
        Assert.Equal(0, meta.Offset);
        Assert.Equal(1471199, meta.Count);
        Assert.Equal(181, meta.Took);
        Assert.False(document.RootElement.TryGetProperty("data", out _));
        Assert.False(document.RootElement.TryGetProperty("links", out _));
    }

    [Fact]
    public void TheUnauthorizedErrorBodyParsesIntoOneApiError()
    {
        IReadOnlyList<ABConnectApiError> errors =
            ABConnectResponseMapper.ParseErrors(ReadFixture("error-401-unauthorized.json"));

        ABConnectApiError error = Assert.Single(errors);
        Assert.Equal("401", error.Status);
        Assert.Equal("Unauthorized", error.Title);
        Assert.Equal("Signature is not authorized.", error.Detail);
        Assert.Null(error.SourcePointer);
        Assert.Null(error.SourceParameter);
    }

    [Fact]
    public void TheNotFoundErrorBodyKeepsItsErrorDespiteANullDetail()
    {
        // Gate G-5's supplementary capture: the live 404 body has "detail": null. Dropping the error
        // because one member is null would leave ABConnectNotFoundException.Errors empty and the
        // status code the only evidence of what happened.
        IReadOnlyList<ABConnectApiError> errors =
            ABConnectResponseMapper.ParseErrors(ReadFixture("error-404-not-found.json"));

        ABConnectApiError error = Assert.Single(errors);
        Assert.Equal("404", error.Status);
        Assert.Equal("Not Found", error.Title);
        Assert.Null(error.Detail);
    }

    [Fact]
    public void AnErrorBodyWithASourceBlockKeepsThePointerAndTheParameter()
    {
        IReadOnlyList<ABConnectApiError> errors =
            ABConnectResponseMapper.ParseErrors(ReadFixture("error-400-invalid-filter.json"));

        Assert.Equal(2, errors.Count);

        Assert.Equal("400", errors[0].Status);
        Assert.Equal("Bad Request", errors[0].Title);
        Assert.Equal("Unknown property 'document.titel' in filter expression.", errors[0].Detail);
        Assert.Equal("/data/attributes/document.titel", errors[0].SourcePointer);
        Assert.Equal("filter[standards]", errors[0].SourceParameter);

        Assert.Null(errors[1].SourcePointer);
        Assert.Equal("fields[standards]", errors[1].SourceParameter);
    }

    [Fact]
    public void ABodyThatIsNotAJsonApiErrorDocumentParsesToAnEmptyList()
    {
        // An error body arrives exactly when something has already gone wrong, so parsing it must be
        // total. A gateway's HTML page yields no errors rather than an exception that would replace
        // the real failure with a parse failure.
        Assert.Empty(ABConnectResponseMapper.ParseErrors(ReadFixture("error-non-json-body.txt")));
        Assert.Empty(ABConnectResponseMapper.ParseErrors("{}"));
        Assert.Empty(ABConnectResponseMapper.ParseErrors(string.Empty));
        Assert.Empty(ABConnectResponseMapper.ParseErrors(null));
    }

    [Fact]
    public void EveryFixtureIsValidJsonAndCarriesItsProvenanceSibling()
    {
        string[] payloads = Directory.GetFiles(FixturesDirectory, "*.json");
        Assert.NotEmpty(payloads);

        foreach (string payload in payloads)
        {
            using JsonDocument parsed = JsonDocument.Parse(File.ReadAllText(payload));
            Assert.NotEqual(JsonValueKind.Undefined, parsed.RootElement.ValueKind);

            string provenance = Path.ChangeExtension(payload, ".source");
            Assert.True(
                File.Exists(provenance),
                $"Fixture '{Path.GetFileName(payload)}' has no sibling .source file naming where it came from.");
        }
    }

    [Fact]
    public void NoFixtureContainsACredentialOrASignature()
    {
        // AB Connect echoes the request query back in links.self, so a captured payload can carry
        // partner.id and auth.signature. AC-16 forbids either reaching a log, and a committed fixture
        // is a permanent log.
        string[] forbidden = ["partner.id", "partner.key", "auth.signature", "auth.expires"];

        foreach (string fixture in Directory.GetFiles(FixturesDirectory))
        {
            string content = File.ReadAllText(fixture);
            foreach (string token in forbidden)
            {
                Assert.DoesNotContain(token, content, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// Deserializes with the SDK's own options, so a test exercises the same source-generated
    /// metadata, converters and naming policy the client uses at run time.
    /// </summary>
    private static T Deserialize<T>(string json)
        where T : class
        => JsonSerializer.Deserialize<T>(json, ABConnectJson.Default)
            ?? throw new InvalidOperationException($"'{typeof(T).Name}' deserialized to null.");

    /// <summary>Reads one fixture payload as text.</summary>
    private static string ReadFixture(string fileName)
        => File.ReadAllText(Path.Combine(FixturesDirectory, fileName));

    /// <summary>
    /// The directory holding the golden payloads.
    /// </summary>
    private static string FixturesDirectory { get; } = ResolveFixturesDirectory();

    /// <summary>
    /// Locates <c>Fixtures/</c> without depending on the payloads being copied to the build output.
    /// The source location is tried first, which also works under <c>--artifacts-path</c> where the
    /// output directory is nowhere near the project; the output directory is tried second so the
    /// tests keep working if the project later copies the payloads.
    /// </summary>
    /// <param name="thisFilePath">Injected by the compiler; do not pass a value.</param>
    private static string ResolveFixturesDirectory([CallerFilePath] string thisFilePath = "")
    {
        List<string> candidates = [];

        string? sourceDirectory = Path.GetDirectoryName(thisFilePath);
        if (!string.IsNullOrEmpty(sourceDirectory))
        {
            candidates.Add(Path.Combine(sourceDirectory, "Fixtures"));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "Fixtures"));

        DirectoryInfo? walk = new(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && walk is not null; depth++, walk = walk.Parent)
        {
            candidates.Add(Path.Combine(walk.FullName, "Fixtures"));
        }

        foreach (string candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find the test Fixtures directory. Looked in: " + string.Join(", ", candidates));
    }
}
