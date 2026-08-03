using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OnCourse.ABConnect.Feed;
using OnCourse.ABConnect.Models;
using OnCourse.ABConnect.Queries;
using OnCourse.ABConnect.Tests.Fakes;
using Xunit;

namespace OnCourse.ABConnect.Tests;

/// <summary>
/// Section 6.2 and AC-8: the standards traversal pages by offset under a deterministic two-key sort,
/// keeps deleted standards in scope, never asks for the wildcard field set, and refuses to hand back a
/// snapshot it cannot prove is complete.
/// </summary>
/// <remarks>
/// Every test drives the real <see cref="ABConnectFeed"/> against
/// <see cref="FakeABConnectClient"/>, which renders each query it is handed through the real
/// <see cref="ABQueryStringBuilder"/>. The wire-form assertions are therefore on the string that would
/// actually have been sent, with no HTTP involved.
/// </remarks>
public sealed class StandardsPagingTests
{
    private const string DocumentGuid = "9D85340C-B0E5-4C0A-9A1B-2C3D4E5F6A7B";

    private static readonly DateTimeOffset TraversalStart =
        new(2026, 8, 2, 13, 45, 0, TimeSpan.Zero);

    private static ABConnectFeed CreateFeed(
        FakeABConnectClient client,
        ABConnectOptions options,
        TimeProvider? timeProvider = null)
        => new(
            client,
            Options.Create(options),
            timeProvider ?? new FakeTimeProvider(TraversalStart),
            NullLogger<ABConnectFeed>.Instance);

    /// <summary>A distinct, well-formed AB Connect GUID for row number <paramref name="ordinal"/>.</summary>
    private static string RowGuid(int ordinal)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"9D85340C-B0E5-4C0A-9A1B-{ordinal:D12}");

    /// <summary>
    /// Serves the given pages of standards, echoing <paramref name="echoedLimit"/> as
    /// <c>meta.limit</c> and <paramref name="reportedCount"/> as <c>meta.count</c> on every page.
    /// </summary>
    private static void ServePages(
        FakeABConnectClient client,
        int reportedCount,
        int echoedLimit,
        params string[][] pages)
    {
        client.OnGetStandards = (query, call) =>
        {
            int index = call - 1;
            string[] guids = index < pages.Length ? pages[index] : [];

            return FakeABConnectClient.StandardsPage(
                guids.Select(FakeABConnectClient.StandardWithGuid),
                reportedCount,
                echoedLimit,
                query.Page.Offset);
        };
    }

    [Fact]
    public async Task ADuplicateGuidAcrossPagesThrowsABConnectPagingException()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);

        // Page two repeats a row from page one, which is what an offset walk sees when the result set
        // shifts underneath it.
        ServePages(
            client,
            reportedCount: 4,
            echoedLimit: 2,
            [RowGuid(1), RowGuid(2)],
            [RowGuid(2), RowGuid(3)]);

        ABConnectFeed feed = CreateFeed(client, options);

        ABConnectPagingException failure = await Assert.ThrowsAsync<ABConnectPagingException>(
            () => feed.ReadDocumentSnapshotAsync(DocumentGuid, new DocumentReadOptions { PageSize = 2 }));

        Assert.Contains(RowGuid(2), failure.Message, StringComparison.Ordinal);
        Assert.Contains("twice", failure.Message, StringComparison.Ordinal);

        // Detected on the page it arrived on, so the walk stops there rather than reading the rest of
        // an inconsistent document.
        Assert.Equal(2, client.RequestCount);
    }

    [Fact]
    public async Task AGuidRepeatedWithDifferentCasingIsStillADuplicate()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        ServePages(
            client,
            reportedCount: 4,
            echoedLimit: 2,
            [RowGuid(1).ToLowerInvariant(), RowGuid(2)],
            [RowGuid(1).ToUpperInvariant(), RowGuid(3)]);

        ABConnectFeed feed = CreateFeed(client, options);

        // Identities compare case-insensitively, so a page that changes GUID casing cannot manufacture
        // a "distinct" row and slip past the completeness check.
        await Assert.ThrowsAsync<ABConnectPagingException>(
            () => feed.ReadDocumentSnapshotAsync(DocumentGuid, new DocumentReadOptions { PageSize = 2 }));
    }

    [Fact]
    public async Task ACollectedCountBelowMetaCountThrowsWithZeroTolerance()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);

        // AB Connect reports six standards and then delivers five, which is what a document edited
        // between the first page and the last looks like from the outside.
        ServePages(
            client,
            reportedCount: 6,
            echoedLimit: 2,
            [RowGuid(1), RowGuid(2)],
            [RowGuid(3), RowGuid(4)],
            [RowGuid(5)]);

        ABConnectFeed feed = CreateFeed(client, options);

        ABConnectPagingException failure = await Assert.ThrowsAsync<ABConnectPagingException>(
            () => feed.ReadDocumentSnapshotAsync(DocumentGuid, new DocumentReadOptions { PageSize = 2 }));

        Assert.Contains("5", failure.Message, StringComparison.Ordinal);
        Assert.Contains("6", failure.Message, StringComparison.Ordinal);
        Assert.Contains("tolerance is zero", failure.Message, StringComparison.Ordinal);
        Assert.Contains(DocumentGuid, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOvershootBeyondMetaCountAlsoThrows()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);

        // Three reported, four delivered. The check is an equality, not a floor.
        ServePages(
            client,
            reportedCount: 3,
            echoedLimit: 2,
            [RowGuid(1), RowGuid(2)],
            [RowGuid(3), RowGuid(4)],
            []);

        ABConnectFeed feed = CreateFeed(client, options);

        await Assert.ThrowsAsync<ABConnectPagingException>(
            () => feed.ReadDocumentSnapshotAsync(DocumentGuid, new DocumentReadOptions { PageSize = 2 }));
    }

    [Fact]
    public async Task TheWalkStopsAtAShortPage()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        ServePages(
            client,
            reportedCount: 3,
            echoedLimit: 2,
            [RowGuid(1), RowGuid(2)],
            [RowGuid(3)]);

        ABConnectFeed feed = CreateFeed(client, options);

        DocumentSnapshot snapshot = await feed.ReadDocumentSnapshotAsync(
            DocumentGuid,
            new DocumentReadOptions { PageSize = 2 });

        // Two requests, not three: the one-row page is short, so it is the last page and no confirming
        // request is issued.
        Assert.Equal(2, client.RequestCount);
        Assert.Equal(2, snapshot.PagesFetched);
        Assert.Equal(3, snapshot.Standards.Count);
        Assert.Equal(3, snapshot.ReportedTotalCount);
        Assert.Equal(DocumentGuid, snapshot.DocumentGuid);
        Assert.Equal(new[] { 0, 2 }, client.StandardsQueries.Select(q => q.Page.Offset));
    }

    [Fact]
    public async Task TheRequestCountCeilingIsEnforced()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);

        int nextRow = 0;

        // A thousand standards reported at a limit of 100 buys a budget of ceil(1000 / 100) + 2 = 12
        // requests. The service then quietly drops to ten rows per page, so the walk can neither
        // account for the reported count nor detect a short page, and the budget is what stops it.
        client.OnGetStandards = (query, call) =>
        {
            int rows = call == 1 ? 100 : 10;
            List<Standard> page = [];
            for (int index = 0; index < rows; index++)
            {
                page.Add(FakeABConnectClient.StandardWithGuid(RowGuid(++nextRow)));
            }

            return FakeABConnectClient.StandardsPage(page, 1000, rows, query.Page.Offset);
        };

        ABConnectFeed feed = CreateFeed(client, options);

        ABConnectPagingException failure = await Assert.ThrowsAsync<ABConnectPagingException>(
            () => feed.ReadDocumentSnapshotAsync(DocumentGuid));

        Assert.Equal(12, client.RequestCount);
        Assert.Contains("12", failure.Message, StringComparison.Ordinal);
        Assert.Contains("ceil(count / limit) + 2", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryRequestCarriesTheTwoKeySortTheStatusScopeAndNeverTheWildcard()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        ServePages(
            client,
            reportedCount: 5,
            echoedLimit: 2,
            [RowGuid(1), RowGuid(2)],
            [RowGuid(3), RowGuid(4)],
            [RowGuid(5)]);

        ABConnectFeed feed = CreateFeed(client, options);

        await feed.ReadDocumentSnapshotAsync(DocumentGuid, new DocumentReadOptions { PageSize = 2 });

        Assert.Equal(3, client.StandardsRequestUris.Count);

        Assert.All(client.StandardsRequestUris, uri =>
        {
            // Defect 7: a two-key sort on every request, so offset paging runs against a total order
            // rather than against AB Connect's relevance ordering.
            Assert.Equal("seq,guid", FakeABConnectClient.ParameterValue(uri, "sort[standards]"));
            Assert.Contains("sort[standards]=seq,guid", uri, StringComparison.Ordinal);

            // Defect 8: deleted standards are in scope on every request, so a deletion is visible.
            Assert.Equal(
                $"((document.guid EQ '{DocumentGuid}') AND (status IN ('active','deleted')))",
                FakeABConnectClient.DecodedParameterValue(uri, "filter[standards]"));

            // Defect 5: the .guid spelling, never .id.
            Assert.Contains("document.guid", FakeABConnectClient.DecodedParameterValue(uri, "filter[standards]"), StringComparison.Ordinal);
            Assert.DoesNotContain(".id", uri, StringComparison.Ordinal);

            // Defect 6 and AC-6: never the wildcard field set, neither literally nor escaped.
            Assert.NotEqual("*", FakeABConnectClient.ParameterValue(uri, "fields[standards]"));
            Assert.DoesNotContain("fields[standards]=*", uri, StringComparison.Ordinal);
            Assert.DoesNotContain("*", uri, StringComparison.Ordinal);
            Assert.DoesNotContain("%2A", uri, StringComparison.OrdinalIgnoreCase);
        });

        Assert.All(client.StandardsQueries, query =>
        {
            Assert.Equal(StandardSort.Default, query.Sort);
            Assert.Equal(StandardStatusScope.ActiveAndDeleted, query.Status);
            Assert.Equal(StandardFieldSet.Snapshot, query.Fields);
            Assert.False(query.Fields.IsWildcard);
        });
    }

    [Fact]
    public async Task AWildcardFieldSetIsRefusedBeforeAnyRequestReachesTheService()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        client.OnGetStandards = (_, _) => FakeABConnectClient.StandardsPage([], 0);

        ABConnectFeed feed = CreateFeed(client, options);

        // AllowWildcardFields defaults to false, so fields[standards]=* is unreachable even when a
        // caller asks for it explicitly.
        await Assert.ThrowsAsync<ABConnectConfigurationException>(
            () => feed.ReadDocumentSnapshotAsync(
                DocumentGuid,
                new DocumentReadOptions { Fields = StandardFieldSet.Wildcard }));
    }

    [Fact]
    public async Task OffsetsAdvanceByTheLimitTheServiceEchoedRatherThanTheOneRequested()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);

        // The request asks for 100 rows a page; AB Connect applies 25 and says so in meta.limit. The
        // offset arithmetic follows the echo, because that is the arithmetic AB Connect itself used.
        int nextRow = 0;
        client.OnGetStandards = (query, _) =>
        {
            List<Standard> page = [];
            for (int index = 0; index < 25; index++)
            {
                page.Add(FakeABConnectClient.StandardWithGuid(RowGuid(++nextRow)));
            }

            return FakeABConnectClient.StandardsPage(page, 75, 25, query.Page.Offset);
        };

        ABConnectFeed feed = CreateFeed(client, options);

        DocumentSnapshot snapshot = await feed.ReadDocumentSnapshotAsync(DocumentGuid);

        Assert.Equal(new[] { 0, 25, 50 }, client.StandardsQueries.Select(q => q.Page.Offset));
        Assert.Equal(new[] { "0", "25", "50" }, client.StandardsRequestUris.Select(uri => FakeABConnectClient.ParameterValue(uri, "offset")));
        Assert.Equal(75, snapshot.Standards.Count);
        Assert.Equal(3, snapshot.PagesFetched);
    }

    [Fact]
    public async Task ADocumentWithNoStandardsIsASuccessfulSnapshotAndNotAFailure()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        client.OnGetStandards = (_, _) => FakeABConnectClient.StandardsPage([], 0);

        ABConnectFeed feed = CreateFeed(client, options);

        DocumentSnapshot snapshot = await feed.ReadDocumentSnapshotAsync(DocumentGuid);

        // Invariant E1: an empty snapshot means AB Connect answered and the document has no standards
        // in the requested status scope. It is not a failure and it is not a default-constructed
        // envelope.
        Assert.Empty(snapshot.Standards);
        Assert.Equal(0, snapshot.ReportedTotalCount);
        Assert.Equal(1, snapshot.PagesFetched);
        Assert.Equal(DocumentGuid, snapshot.DocumentGuid);
    }

    [Fact]
    public async Task FetchedAtUtcIsStampedFromTheInjectedClockBeforeTheFirstRequest()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        FakeTimeProvider clock = new(TraversalStart);

        int nextRow = 0;
        client.OnGetStandards = (query, call) =>
        {
            // Each request costs thirty seconds of virtual time, so a snapshot stamped at the end
            // would carry a visibly different instant.
            clock.Advance(TimeSpan.FromSeconds(30));

            List<Standard> page = [];
            int rows = call == 1 ? 2 : 1;
            for (int index = 0; index < rows; index++)
            {
                page.Add(FakeABConnectClient.StandardWithGuid(RowGuid(++nextRow)));
            }

            return FakeABConnectClient.StandardsPage(page, 3, 2, query.Page.Offset);
        };

        ABConnectFeed feed = CreateFeed(client, options, clock);

        DocumentSnapshot snapshot = await feed.ReadDocumentSnapshotAsync(
            DocumentGuid,
            new DocumentReadOptions { PageSize = 2 });

        Assert.Equal(TraversalStart, snapshot.FetchedAtUtc);
        Assert.Equal(TraversalStart.AddMinutes(1), clock.GetUtcNow());
    }

    [Fact]
    public async Task TheStreamingFormAlsoDetectsADuplicateAcrossPages()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        ServePages(
            client,
            reportedCount: 4,
            echoedLimit: 2,
            [RowGuid(1), RowGuid(2)],
            [RowGuid(2), RowGuid(3)]);

        ABConnectFeed feed = CreateFeed(client, options);

        List<StandardPage> pages = [];

        await Assert.ThrowsAsync<ABConnectPagingException>(async () =>
        {
            await foreach (StandardPage page in feed.ReadDocumentPagesAsync(
                DocumentGuid,
                new DocumentReadOptions { PageSize = 2 }).ConfigureAwait(false))
            {
                pages.Add(page);
            }
        });

        // The first page was real and was yielded before the failure; the failure came from the
        // iteration that hit it rather than ending the enumeration quietly.
        Assert.Single(pages);
        Assert.Equal(1, pages[0].PageNumber);
    }

    [Fact]
    public async Task BreakingOutOfTheStreamingFormEarlyIssuesNoFurtherRequest()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        ServePages(
            client,
            reportedCount: 6,
            echoedLimit: 2,
            [RowGuid(1), RowGuid(2)],
            [RowGuid(3), RowGuid(4)],
            [RowGuid(5), RowGuid(6)]);

        ABConnectFeed feed = CreateFeed(client, options);

        await foreach (StandardPage page in feed.ReadDocumentPagesAsync(
            DocumentGuid,
            new DocumentReadOptions { PageSize = 2 }))
        {
            Assert.Equal(2, page.Standards.Count);
            break;
        }

        Assert.Equal(1, client.RequestCount);
    }

    [Fact]
    public async Task InvalidArgumentsAreRejectedBeforeAnyRequestIsIssued()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        ABConnectFeed feed = CreateFeed(client, options);

        // A stray quote can never reach a filter, on either the buffering or the streaming form.
        await Assert.ThrowsAsync<ArgumentException>(
            () => feed.ReadDocumentSnapshotAsync("9D85340C-B0E5-4C0A-9A1B-2C3D4E5F6A7'"));
        Assert.Throws<ArgumentException>(
            () => { _ = feed.ReadDocumentPagesAsync("9D85340C-B0E5-4C0A-9A1B-2C3D4E5F6A7'"); });

        // A page size of zero would return the meta block and no rows, reporting an empty document
        // instead of reading it.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => feed.ReadDocumentSnapshotAsync(DocumentGuid, new DocumentReadOptions { PageSize = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => { _ = feed.ReadDocumentPagesAsync(DocumentGuid, new DocumentReadOptions { PageSize = 0 }); });

        Assert.Equal(0, client.RequestCount);
    }

    [Fact]
    public async Task ANarrowedFieldSetStillCarriesAnIdentityForTheDuplicateCheck()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);

        // Fields narrowed so far that attributes.guid is absent. The JSON:API resource id is the
        // fallback identity, so narrowing the field set cannot disarm the duplicate check.
        client.OnGetStandards = (query, _) => FakeABConnectClient.StandardsPage(
            [new Standard { Id = RowGuid(1), Type = "standards" }],
            2,
            1,
            query.Page.Offset);

        ABConnectFeed feed = CreateFeed(client, options);

        ABConnectPagingException failure = await Assert.ThrowsAsync<ABConnectPagingException>(
            () => feed.ReadDocumentSnapshotAsync(
                DocumentGuid,
                new DocumentReadOptions { Fields = StandardFieldSet.Identity, PageSize = 1 }));

        Assert.Contains(RowGuid(1), failure.Message, StringComparison.Ordinal);
        Assert.Contains("twice", failure.Message, StringComparison.Ordinal);
    }
}
