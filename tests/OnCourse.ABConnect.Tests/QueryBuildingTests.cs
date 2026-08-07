using OnCourse.ABConnect.Http;
using OnCourse.ABConnect.Models;
using OnCourse.ABConnect.Queries;
using OnCourse.ABConnect.Tests.Fakes;
using Xunit;

namespace OnCourse.ABConnect.Tests;

/// <summary>
/// Section 5.4 and defects 4, 5, and 6: no caller can concatenate a filter, a stray quote cannot reach
/// one, the <c>.guid</c> spelling is the only spelling emitted, the filter expression is percent-encoded
/// exactly once, the page window is clamped, and <c>*</c> is unreachable unless it has been explicitly
/// permitted.
/// </summary>
/// <remarks>
/// The exact-string assertions here are deliberate. A test that only checked "the filter is escaped"
/// would pass against a builder that escaped it twice, which is the very bug defect 5 describes, so the
/// whole expected query string is spelled out and compared byte for byte.
/// </remarks>
public sealed class QueryBuildingTests
{
    private const string DocumentGuid = "9D85340C-B0E5-4C0A-9A1B-2C3D4E5F6A7B";
    private const string AuthorityGuid = "1A2B3C4D-5E6F-4A0B-8C9D-0E1F2A3B4C5D";

    /// <summary>
    /// The percent-encoding of the default status term, <c>(status IN ('active','deleted','obsolete'))</c>,
    /// escaped exactly once.
    /// </summary>
    private const string EncodedStatusTerm =
        "%28status%20IN%20%28%27active%27%2C%27deleted%27%2C%27obsolete%27%29%29";

    /// <summary>The <c>fields[standards]</c> value of <see cref="StandardFieldSet.Identity"/>.</summary>
    private const string IdentityFields = "guid,status,date_modified_utc";

    [Theory]
    [InlineData("9D85340C-B0E5-4C0A-9A1B-2C3D4E5F6A7'")]
    [InlineData("' OR 1 EQ 1 --")]
    [InlineData("9D85340C'--")]
    [InlineData("9D85340C-B0E5-4C0A-9A1B-2C3D4E5F6A7B' AND (status EQ 'active')")]
    public void AGuidContainingASingleQuoteIsRejectedByEveryFactory(string hostile)
    {
        Assert.Throws<ArgumentException>(() => StandardsFilter.ByDocument(hostile));
        Assert.Throws<ArgumentException>(() => StandardsFilter.ByPublication(hostile));
        Assert.Throws<ArgumentException>(() => StandardsFilter.ByAuthority(hostile));
        Assert.Throws<ArgumentException>(() => StandardsFilter.BySection(hostile));
        Assert.Throws<ArgumentException>(() => StandardsFilter.ValidateGuid(hostile, "guid"));
        Assert.Throws<ArgumentException>(
            () => ABQueryStringBuilder.BuildStandardLookup(hostile, StandardFieldSet.Identity, new ABConnectOptions()));
    }

    [Fact]
    public void AGuidThatIsEmptyOrTheWrongLengthIsRejected()
    {
        Assert.Throws<ArgumentException>(() => StandardsFilter.ByDocument(string.Empty));
        Assert.Throws<ArgumentException>(() => StandardsFilter.ByDocument("   "));
        Assert.Throws<ArgumentException>(() => StandardsFilter.ByDocument("9D85340C"));
        Assert.Throws<ArgumentException>(
            () => StandardsFilter.ByDocument("9D85340C-B0E5-4C0A-9A1B-2C3D4E5F6A7B-EXTRA"));

        // Hexadecimal digits and dashes only. A 'Z' is not a GUID character.
        Assert.Throws<ArgumentException>(() => StandardsFilter.ByDocument("ZD85340C-B0E5-4C0A-9A1B-2C3D4E5F6A7B"));
    }

    [Fact]
    public void ALimitAboveOneHundredIsClampedAtConstructionAndInTheEmittedUri()
    {
        PageRequest page = new(0, 5_000);

        // AB Connect's documented maximum is 100 and the clamp is silent, so no caller can ask for a
        // page the service will quietly shrink.
        Assert.Equal(100, page.Limit);
        Assert.Equal(100, PageRequest.MaxLimit);

        (string requestUri, _) = ABQueryStringBuilder.Build(
            new StandardsQuery { Fields = StandardFieldSet.Identity, Page = page },
            new ABConnectOptions());

        Assert.Equal("100", FakeABConnectClient.ParameterValue(requestUri, "limit"));
    }

    [Fact]
    public void ANegativeOffsetThrowsRatherThanBecomingASentinel()
    {
        // Version 2's ParseOffset handed -1 straight back into the next call. A negative offset is now
        // rejected at construction, so no sentinel can reach a request URI at all.
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageRequest(-1, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageRequest(int.MinValue, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageRequest(0, -1));
    }

    [Fact]
    public void TheConfiguredPageSizeLowersTheLimitButNeverRaisesIt()
    {
        ABConnectOptions small = new() { PageSize = 25 };

        (string lowered, _) = ABQueryStringBuilder.Build(
            new StandardsQuery { Fields = StandardFieldSet.Identity, Page = PageRequest.First },
            small);
        Assert.Equal("25", FakeABConnectClient.ParameterValue(lowered, "limit"));

        // A deliberately low limit is never raised to the configured page size.
        (string deliberate, _) = ABQueryStringBuilder.Build(
            new StandardsQuery { Fields = StandardFieldSet.Identity, Page = new PageRequest(0, 10) },
            small);
        Assert.Equal("10", FakeABConnectClient.ParameterValue(deliberate, "limit"));

        // MetaOnly asks for the counters and no rows.
        (string metaOnly, _) = ABQueryStringBuilder.Build(
            new StandardsQuery { Fields = StandardFieldSet.Identity, Page = PageRequest.MetaOnly },
            new ABConnectOptions());
        Assert.Equal("0", FakeABConnectClient.ParameterValue(metaOnly, "limit"));
        Assert.Equal("0", FakeABConnectClient.ParameterValue(metaOnly, "offset"));
    }

    [Fact]
    public void TheWildcardFieldSetThrowsUnlessAllowWildcardFieldsIsSet()
    {
        ABConnectOptions denied = new();

        ABConnectConfigurationException failure = Assert.Throws<ABConnectConfigurationException>(
            () => ABQueryStringBuilder.Build(
                new StandardsQuery { Fields = StandardFieldSet.Wildcard },
                denied));

        // The message has to name the option a caller would have to set, or the failure is a dead end.
        Assert.Contains(nameof(ABConnectOptions.AllowWildcardFields), failure.Message, StringComparison.Ordinal);
        Assert.Contains(ABConnectOptions.SectionName, failure.Message, StringComparison.Ordinal);

        // The gate looks for the token anywhere in the set, not only at StandardFieldSet.Wildcard, so a
        // hand-built set carrying '*' alongside real names is gated too.
        Assert.Throws<ABConnectConfigurationException>(
            () => ABQueryStringBuilder.Build(
                new StandardsQuery { Fields = StandardFieldSet.Of("guid", "*") },
                denied));

        // The same gate applies to a lookup and to a facet summary.
        Assert.Throws<ABConnectConfigurationException>(
            () => ABQueryStringBuilder.BuildStandardLookup(DocumentGuid, StandardFieldSet.Wildcard, denied));
        Assert.Throws<ABConnectConfigurationException>(
            () => ABQueryStringBuilder.Build(new FacetQuery<Region> { FacetName = "*" }, denied));
    }

    [Fact]
    public void AWildcardEventFieldSetIsGatedExactlyLikeAWildcardStandardFieldSet()
    {
        // AC-6 names fields[events]=* alongside fields[standards]=*. AB Connect throttles the wildcard
        // by the value of the fields parameter, not by the resource it was asked of, so an events query
        // must not be able to emit '*' just because EventFieldSet has no Wildcard member of its own.
        ABConnectOptions denied = new();

        ABConnectConfigurationException failure = Assert.Throws<ABConnectConfigurationException>(
            () => ABQueryStringBuilder.Build(
                new EventsQuery { AfterSequence = 5, Fields = EventFieldSet.Of("*") },
                denied));

        Assert.Contains("fields[events]", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ABConnectOptions.AllowWildcardFields), failure.Message, StringComparison.Ordinal);
        Assert.Contains(ABConnectOptions.SectionName, failure.Message, StringComparison.Ordinal);

        // The token is looked for anywhere in the set, not only as the whole set.
        Assert.Throws<ABConnectConfigurationException>(
            () => ABQueryStringBuilder.Build(
                new EventsQuery { AfterSequence = 5, Fields = EventFieldSet.Of("seq", "*") },
                denied));

        // The default set is not a wildcard and is not gated.
        (_, ABConnectRequestContext plain) = ABQueryStringBuilder.Build(
            new EventsQuery { AfterSequence = 5 },
            denied);
        Assert.False(plain.IsWildcardRequest);
    }

    [Fact]
    public void APermittedWildcardEventsQueryIsMarkedSoItSpendsTheNarrowBucket()
    {
        ABConnectOptions permitted = new() { AllowWildcardFields = true };

        (string requestUri, ABConnectRequestContext context) = ABQueryStringBuilder.Build(
            new EventsQuery { AfterSequence = 5, Fields = EventFieldSet.Of("*") },
            permitted);

        Assert.Equal("*", FakeABConnectClient.ParameterValue(requestUri, "fields[events]"));

        // Without this flag the two-per-second bucket is never charged and a discovery run drains the
        // bucket the production sync depends on.
        Assert.True(context.IsWildcardRequest);
    }

    [Fact]
    public void AWildcardRequestIsMarkedOnTheRequestContextOnceItIsPermitted()
    {
        ABConnectOptions permitted = new() { AllowWildcardFields = true };

        (string requestUri, ABConnectRequestContext context) = ABQueryStringBuilder.Build(
            new StandardsQuery { Fields = StandardFieldSet.Wildcard, Page = PageRequest.MetaOnly },
            permitted);

        Assert.Equal("*", FakeABConnectClient.ParameterValue(requestUri, "fields[standards]"));

        // The throttle handler charges the two-per-second bucket off this flag, so it has to be set.
        Assert.True(context.IsWildcardRequest);

        (_, ABConnectRequestContext narrow) = ABQueryStringBuilder.Build(
            new StandardsQuery { Fields = StandardFieldSet.Identity },
            permitted);
        Assert.False(narrow.IsWildcardRequest);
    }

    [Fact]
    public void EveryFilterFactoryUsesTheGuidSpellingAndNeverTheIdSpelling()
    {
        Assert.Equal("document.guid", StandardsFilter.DocumentGuidField);
        Assert.Equal("document.publication.guid", StandardsFilter.PublicationGuidField);
        Assert.Equal("document.publication.authorities.guid", StandardsFilter.AuthorityGuidField);
        Assert.Equal("section.guid", StandardsFilter.SectionGuidField);

        StandardsFilter[] filters =
        [
            StandardsFilter.ByDocument(DocumentGuid),
            StandardsFilter.ByPublication(DocumentGuid),
            StandardsFilter.ByAuthority(AuthorityGuid),
            StandardsFilter.BySection(DocumentGuid),
        ];

        foreach (StandardsFilter filter in filters)
        {
            (string requestUri, _) = ABQueryStringBuilder.Build(
                new StandardsQuery { Filter = filter, Fields = StandardFieldSet.Identity },
                new ABConnectOptions());

            string expression = FakeABConnectClient.DecodedParameterValue(requestUri, "filter[standards]");

            Assert.EndsWith(".guid", filter.Terms[0].Field, StringComparison.Ordinal);
            Assert.Contains(".guid EQ ", expression, StringComparison.Ordinal);
            Assert.DoesNotContain(".id EQ ", expression, StringComparison.Ordinal);
            Assert.DoesNotContain(".id", requestUri, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheDefaultStandardsQueryEmitsTheExactExpectedQueryString()
    {
        (string requestUri, ABConnectRequestContext context) = ABQueryStringBuilder.Build(
            new StandardsQuery { Fields = StandardFieldSet.Identity },
            new ABConnectOptions());

        Assert.Equal(
            "standards" +
            "?fields[standards]=" + IdentityFields +
            "&filter[standards]=" + EncodedStatusTerm +
            "&sort[standards]=seq,guid" +
            "&limit=100" +
            "&offset=0",
            requestUri);

        // The credential-free relative URI and the redacted path are the same string, which is what
        // makes ABConnectRequestException.RequestPath safe to log.
        Assert.Equal(requestUri, context.RedactedPath);
    }

    [Fact]
    public void TheWholeFilterExpressionIsPercentEncodedExactlyOnce()
    {
        (string requestUri, _) = ABQueryStringBuilder.Build(
            new StandardsQuery
            {
                Filter = StandardsFilter.ByDocument(DocumentGuid),
                Fields = StandardFieldSet.Identity,
            },
            new ABConnectOptions());

        // Spelled out in full. A second escaping pass would turn every '%' into "%25" and this literal
        // would no longer match, which is exactly the failure defect 5 describes.
        Assert.Equal(
            "standards" +
            "?fields[standards]=" + IdentityFields +
            "&filter[standards]=" +
            "%28%28document.guid%20EQ%20%279D85340C-B0E5-4C0A-9A1B-2C3D4E5F6A7B%27%29" +
            "%20AND%20" + EncodedStatusTerm + "%29" +
            "&sort[standards]=seq,guid" +
            "&limit=100" +
            "&offset=0",
            requestUri);

        // "%25" is the signature of a double-encoded expression, and one unescape pass must recover the
        // plain expression with no percent sign left in it.
        Assert.DoesNotContain("%25", requestUri, StringComparison.OrdinalIgnoreCase);

        string once = FakeABConnectClient.DecodedParameterValue(requestUri, "filter[standards]");
        Assert.Equal(
            $"((document.guid EQ '{DocumentGuid}') AND (status IN ('active','deleted','obsolete')))",
            once);
        Assert.DoesNotContain("%", once, StringComparison.Ordinal);
    }

    [Fact]
    public void ConjoinedFiltersRenderAsOneAndExpressionWithTheStatusTermLast()
    {
        StandardsFilter filter = StandardsFilter.ByDocument(DocumentGuid)
            .And(StandardsFilter.BySection(AuthorityGuid));

        (string requestUri, _) = ABQueryStringBuilder.Build(
            new StandardsQuery
            {
                Filter = filter,
                Fields = StandardFieldSet.Identity,
                Status = StandardStatusScope.Active,
            },
            new ABConnectOptions());

        Assert.Equal(
            $"((document.guid EQ '{DocumentGuid}') AND (section.guid EQ '{AuthorityGuid}') AND (status EQ 'active'))",
            FakeABConnectClient.DecodedParameterValue(requestUri, "filter[standards]"));
    }

    [Fact]
    public void AGuidSetFilterRendersAsASingleInTermWithTheStatusTermLast()
    {
        StandardsFilter filter = StandardsFilter.ByStandardGuids([DocumentGuid, AuthorityGuid]);

        (string requestUri, _) = ABQueryStringBuilder.Build(
            new StandardsQuery
            {
                Filter = filter,
                Fields = StandardFieldSet.Identity,
                Status = StandardStatusScope.ActiveAndDeleted,
            },
            new ABConnectOptions());

        // The GUIDs render inside one IN list, in the order given, with the unconditional status term
        // last, exactly as AB Connect's own filter examples spell an IN clause.
        Assert.Equal(
            $"((guid IN ('{DocumentGuid}','{AuthorityGuid}')) AND (status IN ('active','deleted')))",
            FakeABConnectClient.DecodedParameterValue(requestUri, "filter[standards]"));

        // Escaped exactly once: "%25" is the signature of a double-encoded expression.
        Assert.DoesNotContain("%25", requestUri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AGuidSetFilterValidatesEveryMemberAndBoundsTheSetSize()
    {
        Assert.Throws<ArgumentNullException>(() => StandardsFilter.ByStandardGuids(null!));
        Assert.Throws<ArgumentException>(() => StandardsFilter.ByStandardGuids([]));
        Assert.Throws<ArgumentException>(() => StandardsFilter.ByStandardGuids(["not-a-guid"]));

        // A hostile value anywhere in the set is rejected, so no IN list can carry a stray quote.
        Assert.Throws<ArgumentException>(
            () => StandardsFilter.ByStandardGuids([DocumentGuid, "9D85340C-B0E5-4C0A-9A1B-2C3D4E5F6A7B' OR 1 EQ 1"]));

        // The cap is the page size: at the cap is accepted, one over is rejected, so a batch can never
        // outgrow the single page it is meant to fit in.
        string[] atCap = [.. Enumerable.Range(0, StandardsFilter.MaxGuidSetSize).Select(RowGuid)];
        StandardsFilter ok = StandardsFilter.ByStandardGuids(atCap);
        Assert.Single(ok.SetTerms);
        Assert.Equal(StandardsFilter.MaxGuidSetSize, ok.SetTerms[0].Values.Count);

        string[] overCap = [.. Enumerable.Range(0, StandardsFilter.MaxGuidSetSize + 1).Select(RowGuid)];
        Assert.Throws<ArgumentException>(() => StandardsFilter.ByStandardGuids(overCap));
    }

    [Fact]
    public void GuidSetFiltersCompareByValueAndComposeWithAnd()
    {
        Assert.Equal(
            StandardsFilter.ByStandardGuids([DocumentGuid, AuthorityGuid]),
            StandardsFilter.ByStandardGuids([DocumentGuid, AuthorityGuid]));
        Assert.NotEqual(
            StandardsFilter.ByStandardGuids([DocumentGuid]),
            StandardsFilter.ByStandardGuids([AuthorityGuid]));

        // An equality term and a set term combine into one conjunction, each kept in its own list.
        StandardsFilter combined = StandardsFilter.ByDocument(DocumentGuid)
            .And(StandardsFilter.ByStandardGuids([AuthorityGuid]));
        Assert.Single(combined.Terms);
        Assert.Single(combined.SetTerms);
        Assert.False(combined.IsEmpty);
    }

    /// <summary>A distinct, well-formed AB Connect GUID for row number <paramref name="ordinal"/>.</summary>
    private static string RowGuid(int ordinal)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"9D85340C-B0E5-4C0A-9A1B-{ordinal:D12}");

    [Theory]
    [InlineData(StandardStatusScope.Active, "(status EQ 'active')")]
    [InlineData(StandardStatusScope.Deleted, "(status EQ 'deleted')")]
    [InlineData(StandardStatusScope.Obsolete, "(status EQ 'obsolete')")]
    [InlineData(StandardStatusScope.ActiveAndDeleted, "(status IN ('active','deleted'))")]
    [InlineData(StandardStatusScope.All, "(status IN ('active','deleted','obsolete'))")]
    public void EveryStatusScopeEmitsItsDocumentedTermAndTheTermIsNeverOmitted(
        StandardStatusScope scope,
        string expected)
    {
        (string requestUri, _) = ABQueryStringBuilder.Build(
            new StandardsQuery { Fields = StandardFieldSet.Identity, Status = scope },
            new ABConnectOptions());

        Assert.Equal(expected, FakeABConnectClient.DecodedParameterValue(requestUri, "filter[standards]"));
    }

    [Fact]
    public void TheDefaultStatusScopeIsAllSoDeletionsAndObsoleteStandardsAreVisible()
    {
        // Defect 8: AB Connect hides deleted standards unless the filter names them, and its no-filter
        // default drops obsolete standards, so the scope that makes a complete mirror has to be the
        // default rather than an opt-in. All emits status IN ('active','deleted','obsolete').
        Assert.Equal(StandardStatusScope.All, new StandardsQuery().Status);
        Assert.Equal("active", ABStandardStatuses.Active);
        Assert.Equal("deleted", ABStandardStatuses.Deleted);
        Assert.Equal("obsolete", ABStandardStatuses.Obsolete);
    }

    [Fact]
    public void AnEventsQueryEmitsTheExactExpectedQueryString()
    {
        (string requestUri, ABConnectRequestContext context) = ABQueryStringBuilder.Build(
            new EventsQuery { AfterSequence = 123_456 },
            new ABConnectOptions());

        Assert.Equal(
            "events" +
            "?fields[events]=seq,date_utc,change_type,target,guid,document_guid,section_guid," +
            "affected_properties,standard,nondeliverable_standard,deleted_standard" +
            "&filter[events]=%28seq%20GT%20123456%29" +
            "&sort[events]=seq" +
            "&limit=100" +
            "&offset=0",
            requestUri);

        Assert.Equal(requestUri, context.RedactedPath);
        Assert.False(context.IsWildcardRequest);
    }

    [Fact]
    public void ADescendingEventsQueryEmitsMinusSeqToReadTheHeadOfTheFeed()
    {
        (string requestUri, _) = ABQueryStringBuilder.Build(
            new EventsQuery
            {
                AfterSequence = 0,
                Order = EventSequenceOrder.Descending,
                Fields = EventFieldSet.Of("seq", "date_utc"),
                Page = new PageRequest(0, 1),
            },
            new ABConnectOptions());

        Assert.Equal(
            "events" +
            "?fields[events]=seq,date_utc" +
            "&filter[events]=%28seq%20GT%200%29" +
            "&sort[events]=-seq" +
            "&limit=1" +
            "&offset=0",
            requestUri);
    }

    [Fact]
    public void AnEventsQueryWithAStandardScopeConjoinsTheScopeWithTheWatermark()
    {
        (string requestUri, _) = ABQueryStringBuilder.Build(
            new EventsQuery
            {
                AfterSequence = 7,
                StandardScope = StandardsFilter.ByAuthority(AuthorityGuid),
            },
            new ABConnectOptions());

        Assert.Equal(
            $"((seq GT 7) AND (document.publication.authorities.guid EQ '{AuthorityGuid}'))",
            FakeABConnectClient.DecodedParameterValue(requestUri, "filter[events]"));
        Assert.Equal("seq", FakeABConnectClient.ParameterValue(requestUri, "sort[events]"));
    }

    [Fact]
    public void AFacetQueryEmitsTheFacetValuesTheOptionalFilterAndLimitZero()
    {
        (string plain, _) = ABQueryStringBuilder.Build(
            new FacetQuery<Publication> { FacetName = ABFacetNames.Publications },
            new ABConnectOptions());

        // A named facet must use facet= (returns the values under details[]), not facet_summary=
        // (counts only). Exactly one request, no rows, nothing to page.
        Assert.Equal("standards?facet=document.publication&limit=0", plain);

        (string scoped, _) = ABQueryStringBuilder.Build(
            new FacetQuery<SectionSummary>
            {
                FacetName = ABFacetNames.Sections,
                Filter = StandardsFilter.ByAuthority(AuthorityGuid),
            },
            new ABConnectOptions());

        Assert.Equal(
            "standards" +
            "?facet=section" +
            "&filter[standards]=%28document.publication.authorities.guid%20EQ%20%27" + AuthorityGuid + "%27%29" +
            "&limit=0",
            scoped);
    }

    [Fact]
    public void AStandardLookupEmitsTheFieldSetAndNothingElse()
    {
        (string requestUri, ABConnectRequestContext context) = ABQueryStringBuilder.BuildStandardLookup(
            DocumentGuid,
            StandardFieldSet.Identity,
            new ABConnectOptions());

        Assert.Equal($"standards/{DocumentGuid}?fields[standards]={IdentityFields}", requestUri);
        Assert.Equal(requestUri, context.RedactedPath);
    }

    [Fact]
    public void FieldNamesAndSortKeysAreValidatedRatherThanEscaped()
    {
        (string requestUri, _) = ABQueryStringBuilder.Build(
            new StandardsQuery
            {
                Fields = StandardFieldSet.Snapshot,
                Sort = StandardSort.Of("-seq", "guid"),
            },
            new ABConnectOptions());

        // The separating commas and the dots stay literal, matching AB Connect's own documented
        // examples. Escaping them would turn the value into something AB Connect never shows.
        Assert.Equal(
            string.Join(',', StandardFieldSet.Snapshot.Fields),
            FakeABConnectClient.ParameterValue(requestUri, "fields[standards]"));
        Assert.Contains("number.prefix_enhanced", requestUri, StringComparison.Ordinal);
        Assert.Equal("-seq,guid", FakeABConnectClient.ParameterValue(requestUri, "sort[standards]"));

        // The commas separating tokens are literal. The filter VALUE is the only thing escaped, so the
        // %2C that does appear in this URI belongs to the status term and to nothing else.
        Assert.DoesNotContain(
            "%2C",
            FakeABConnectClient.ParameterValue(requestUri, "fields[standards]"),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "%2C",
            FakeABConnectClient.ParameterValue(requestUri, "sort[standards]"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("guid&limit=999")]
    [InlineData("guid=1")]
    [InlineData("guid,seq")]
    [InlineData("statement.descr'")]
    [InlineData("")]
    [InlineData("   ")]
    public void AFieldNameThatIsNotAWellFormedPropertyPathIsRejected(string hostile)
    {
        // Tokens are validated rather than escaped, so a name carrying a query-string separator is a
        // hard error rather than a silently mangled parameter.
        Assert.Throws<ArgumentException>(
            () => ABQueryStringBuilder.Build(
                new StandardsQuery { Fields = StandardFieldSet.Of(hostile) },
                new ABConnectOptions()));
    }

    [Fact]
    public void ANullQueryOrNullOptionsIsRejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => ABQueryStringBuilder.Build((StandardsQuery)null!, new ABConnectOptions()));
        Assert.Throws<ArgumentNullException>(
            () => ABQueryStringBuilder.Build(new StandardsQuery { Fields = StandardFieldSet.Identity }, null!));
        Assert.Throws<ArgumentNullException>(
            () => ABQueryStringBuilder.Build((EventsQuery)null!, new ABConnectOptions()));
    }

    [Fact]
    public void FieldSetsAndSortsCompareByContentSoAssertionsOnThemMeanWhatTheyLookLike()
    {
        // Synthesized record equality would compare the backing list by reference. These are values.
        Assert.Equal(StandardFieldSet.Of("guid", "seq"), StandardFieldSet.Of("guid", "seq"));
        Assert.NotEqual(StandardFieldSet.Of("seq", "guid"), StandardFieldSet.Of("guid", "seq"));
        Assert.Equal(StandardSort.Of("seq", "guid"), StandardSort.Default);
        Assert.Equal(StandardsFilter.ByDocument(DocumentGuid), StandardsFilter.ByDocument(DocumentGuid));
        Assert.NotEqual(StandardsFilter.ByDocument(DocumentGuid), StandardsFilter.BySection(DocumentGuid));
        Assert.True(StandardsFilter.None.IsEmpty);
        Assert.Empty(StandardsFilter.None.Terms);
    }
}
