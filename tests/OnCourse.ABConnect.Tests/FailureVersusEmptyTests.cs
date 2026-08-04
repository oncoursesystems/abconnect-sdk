using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OnCourse.ABConnect.Feed;
using OnCourse.ABConnect.Tests.Fakes;
using OnCourse.ABConnect.Throttling;
using Xunit;

namespace OnCourse.ABConnect.Tests;

/// <summary>
/// The defect described in section 2.1 and Invariant E1. "AB Connect answered and there is nothing
/// past the watermark" and "the read failed" must be two different observable outcomes, and telling
/// them apart must not require inspecting a field of a returned object.
/// </summary>
public sealed class FailureVersusEmptyTests
{
    /// <summary>An events page that reports a successful match of nothing.</summary>
    private const string EmptyEventsPage =
        """
        {"links":{"self":"https://api.abconnect.instructure.com/rest/v4.1/events?limit=100&offset=0",
        "first":null,"prev":null,"next":null,"last":null},
        "meta":{"limit":100,"offset":0,"count":0,"took":9},"data":[]}
        """;

    /// <summary>An events page carrying one event, used as the first page of a two-request walk.</summary>
    private const string OneEventPage =
        """
        {"links":{"self":null,"first":null,"prev":null,"next":null,"last":null},
        "meta":{"limit":100,"offset":0,"count":1,"took":9},
        "data":[{"id":"3e81b2f7-47ac-4d10-9f0b-5c6ad2938e71","type":"events",
        "attributes":{"seq":702955,"date_utc":"2017-11-12 00:00:00","change_type":"added",
        "target":"standard","guid":"1B2C3D4D-592E-11E6-A0F5-48E229C466BA"}}]}
        """;

    /// <summary>The body AB Connect returns for a server-side failure on the events endpoint.</summary>
    private const string ServerErrorBody =
        """
        {"errors":[{"status":"500","title":"Internal Server Error","detail":"Unexpected condition."}]}
        """;

    /// <summary>
    /// The whole point of version 3. A failure on the events endpoint throws, so it can never be
    /// mistaken for a feed that has nothing new.
    /// </summary>
    [Fact]
    public async Task ServerErrorFromEventsThrowsRatherThanReportingAnEmptyFeed()
    {
        using Harness harness = new();
        harness.Handler.EnqueueJson(HttpStatusCode.InternalServerError, ServerErrorBody);

        ABConnectServerException failure = await Assert.ThrowsAsync<ABConnectServerException>(
            () => harness.Feed.ReadEventsAsync(afterSequence: 702954));

        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        Assert.StartsWith("events", failure.RequestPath, StringComparison.Ordinal);
        Assert.Equal(1, failure.Attempts);
        Assert.Single(failure.Errors);
        Assert.Equal("500", failure.Errors[0].Status);
        Assert.Equal(1, harness.Handler.SendCount);
    }

    /// <summary>
    /// The same defect one layer deeper, and the last place it could still hide. A 200 that carries
    /// counters but no <c>data</c> key at all is not a feed with nothing new, it is a body the SDK
    /// cannot read, and the walk must refuse it rather than treat the missing array as an empty page
    /// and stop. Note that <c>meta.count</c> here is 500, so reading this as the end of the feed would
    /// have reported a clean run that silently processed nothing while five hundred events waited.
    /// </summary>
    [Fact]
    public async Task AnEventsPageWithCountersButNoDataArrayThrowsRatherThanEndingTheWalk()
    {
        using Harness harness = new();
        harness.Handler.EnqueueOk("""{"meta":{"limit":100,"offset":0,"count":500,"took":12}}""");

        ABConnectResponseFormatException failure =
            await Assert.ThrowsAsync<ABConnectResponseFormatException>(
                () => harness.Feed.ReadEventsAsync(afterSequence: 702954));

        Assert.StartsWith("events", failure.RequestPath, StringComparison.Ordinal);
        Assert.Equal(1, harness.Handler.SendCount);
    }

    /// <summary>
    /// A 200 whose data array is empty is a successful answer: zero events, no highest sequence, no
    /// exception. Version 2 produced the same value here as it did for the failure above.
    /// </summary>
    [Fact]
    public async Task EmptyEventsPageReturnsAnEmptyBatchAndDoesNotThrow()
    {
        using Harness harness = new();
        harness.Handler.EnqueueOk(EmptyEventsPage);

        EventBatch batch = await harness.Feed.ReadEventsAsync(afterSequence: 702954);

        Assert.Empty(batch.Events);
        Assert.Null(batch.HighestSequence);
        Assert.Equal(702954, batch.AfterSequence);
        Assert.Equal(0, batch.ReportedTotalCount);
        Assert.True(batch.IsComplete);
        Assert.Equal(1, batch.PagesFetched);
        Assert.Equal(1, harness.Handler.SendCount);
    }

    /// <summary>
    /// The test section 9 calls for explicitly: the two outcomes are distinguishable without
    /// inspecting any field of a returned object. <see cref="ClassifyAsync"/> discards the result
    /// entirely, so the only information it can act on is whether the call returned or threw.
    /// </summary>
    [Fact]
    public async Task FailureAndEmptyAreDistinguishableWithoutInspectingTheResult()
    {
        using Harness failing = new();
        failing.Handler.EnqueueJson(HttpStatusCode.InternalServerError, ServerErrorBody);

        using Harness empty = new();
        empty.Handler.EnqueueOk(EmptyEventsPage);

        string failureOutcome = await ClassifyAsync(failing.Feed);
        string emptyOutcome = await ClassifyAsync(empty.Feed);

        Assert.Equal("failed", failureOutcome);
        Assert.Equal("answered", emptyOutcome);
        Assert.NotEqual(failureOutcome, emptyOutcome);
    }

    /// <summary>
    /// Reads the feed and reports which of the two outcomes happened, using only control flow. The
    /// result is discarded without a single member access, which is what makes this the assertion
    /// section 9 asks for rather than a restatement of the two tests above.
    /// </summary>
    private static async Task<string> ClassifyAsync(IABConnectFeed feed)
    {
        try
        {
            _ = await feed.ReadEventsAsync(afterSequence: 702954).ConfigureAwait(false);
            return "answered";
        }
        catch (ABConnectException)
        {
            return "failed";
        }
    }

    /// <summary>
    /// A failure part way through a traversal is not downgraded into a short successful read. The
    /// first page's event is discarded along with the exception, because a partially read delta
    /// cannot be used to advance a watermark.
    /// </summary>
    [Fact]
    public async Task FailureOnTheSecondPageOfATraversalThrowsRatherThanReturningTheFirstPage()
    {
        using Harness harness = new();
        harness.Handler.EnqueueOk(OneEventPage);
        harness.Handler.EnqueueJson(HttpStatusCode.BadGateway, ServerErrorBody);

        await Assert.ThrowsAsync<ABConnectServerException>(
            () => harness.Feed.ReadEventsAsync(afterSequence: 702954));

        Assert.Equal(2, harness.Handler.SendCount);
    }

    /// <summary>
    /// An empty feed yields zero pages from the streaming form, not one empty page, so a caller
    /// enumerating pages cannot mistake "nothing new" for "a page arrived".
    /// </summary>
    [Fact]
    public async Task EmptyFeedYieldsZeroPagesFromTheStreamingForm()
    {
        using Harness harness = new();
        harness.Handler.EnqueueOk(EmptyEventsPage);

        List<EventPage> pages = [];
        await foreach (EventPage page in harness.Feed.ReadEventPagesAsync(afterSequence: 702954))
        {
            pages.Add(page);
        }

        Assert.Empty(pages);
        Assert.Equal(1, harness.Handler.SendCount);
    }

    /// <summary>
    /// A 2xx body that is not a usable AB Connect envelope is a failure, not an empty result. This is
    /// the other half of Invariant E1: an empty result is reserved for a body that really did report
    /// a successful match of nothing.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"data\":[]}")]
    public async Task SuccessfulResponseWithNoUsableEnvelopeThrowsInsteadOfReturningAnEmptyBatch(string body)
    {
        using Harness harness = new();
        harness.Handler.EnqueueOk(body);

        ABConnectResponseFormatException failure = await Assert.ThrowsAsync<ABConnectResponseFormatException>(
            () => harness.Feed.ReadEventsAsync(afterSequence: 702954));

        Assert.Equal(HttpStatusCode.OK, failure.StatusCode);
        Assert.StartsWith("events", failure.RequestPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// A client, a feed, and the fake transport they run over. No handlers are installed, so exactly
    /// one HTTP send happens per client call and <c>Attempts</c> is always one.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ABConnectRateLimiterProvider _rateLimiterProvider;

        public Harness()
        {
            ABConnectOptions options = new()
            {
                PartnerId = "test_account",
                PartnerKey = "ajk84Hjk93h59skaAJ8732",
                Retry = new ABConnectRetryOptions { MaxAttempts = 1 },
            };

            FakeTimeProvider timeProvider = new();
            Handler = new FakeHttpMessageHandler();
            _httpClient = new HttpClient(Handler, disposeHandler: false)
            {
                BaseAddress = options.BaseAddress,
                Timeout = Timeout.InfiniteTimeSpan,
            };

            _rateLimiterProvider = new ABConnectRateLimiterProvider(Options.Create(options), timeProvider);

            ABConnectClient client = new(
                _httpClient,
                Options.Create(options),
                _rateLimiterProvider,
                NullLogger<ABConnectClient>.Instance);

            Feed = new ABConnectFeed(
                client,
                Options.Create(options),
                timeProvider,
                NullLogger<ABConnectFeed>.Instance);
        }

        public FakeHttpMessageHandler Handler { get; }

        public IABConnectFeed Feed { get; }

        public void Dispose()
        {
            _httpClient.Dispose();
            _rateLimiterProvider.Dispose();
            Handler.Dispose();
        }
    }
}
