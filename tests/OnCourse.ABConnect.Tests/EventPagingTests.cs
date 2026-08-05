using Microsoft.Extensions.Logging;
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
/// Section 6.1 and AC-7: the event traversal re-anchors on <c>seq GT &lt;highest seen&gt;</c> with
/// <c>offset=0</c> every time, always requests <c>sort[events]=seq</c>, throws
/// <see cref="ABConnectPagingException"/> on out-of-order or non-advancing sequences, and treats a
/// sequence gap as entirely normal: no error, no warning, no log line.
/// </summary>
/// <remarks>
/// Every test drives the real <see cref="ABConnectFeed"/> against
/// <see cref="FakeABConnectClient"/>. There is no HTTP, no real clock, and no timer, so the whole group
/// runs in microseconds while still asserting on the exact wire form of each request, because the fake
/// renders every query it is handed through the real <see cref="ABQueryStringBuilder"/>.
/// </remarks>
public sealed class EventPagingTests
{
    private static ABConnectFeed CreateFeed(
        FakeABConnectClient client,
        ABConnectOptions options,
        ILogger<ABConnectFeed>? logger = null)
        => new(
            client,
            Options.Create(options),
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero)),
            logger ?? NullLogger<ABConnectFeed>.Instance);

    /// <summary>
    /// Serves pages of events in order, then one empty page. Each page is served as its own response,
    /// so the recorded queries show exactly what the traversal asked for each time.
    /// </summary>
    private static void ServePages(FakeABConnectClient client, int reportedCount, params long[][] pages)
    {
        client.OnGetEvents = (_, call) =>
        {
            int index = call - 1;
            long[] sequences = index < pages.Length ? pages[index] : [];

            return FakeABConnectClient.EventsPage(
                sequences.Select(FakeABConnectClient.EventWithSequence),
                reportedCount);
        };
    }

    [Fact]
    public async Task ReadHeadSequenceReturnsTheNewestSequenceFromOneDescendingRequest()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        client.OnGetEvents = (_, _) =>
            FakeABConnectClient.EventsPage([FakeABConnectClient.EventWithSequence(987654)], reportedCount: 1);

        ABConnectFeed feed = CreateFeed(client, options);

        long? head = await feed.ReadHeadSequenceAsync();

        Assert.Equal(987654, head);
        EventsQuery query = Assert.Single(client.EventsQueries);
        Assert.Equal(EventSequenceOrder.Descending, query.Order);
        Assert.Equal(0, query.AfterSequence);
        Assert.Equal(1, query.Page.Limit);
    }

    [Fact]
    public async Task ReadHeadSequenceReturnsNullWhenTheFeedIsEmpty()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        client.OnGetEvents = (_, _) => FakeABConnectClient.EventsPage([], reportedCount: 0);

        ABConnectFeed feed = CreateFeed(client, options);

        Assert.Null(await feed.ReadHeadSequenceAsync());
        Assert.Single(client.EventsQueries);
    }

    [Fact]
    public async Task ThreePagesReAnchorOnTheHighestSequenceWithOffsetZeroEveryTime()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        ServePages(client, reportedCount: 6, [10, 20], [30, 40], [50, 60]);

        ABConnectFeed feed = CreateFeed(client, options);

        EventBatch batch = await feed.ReadEventsAsync(0, new EventReadOptions { PageSize = 2 });

        // Three pages carried events, and a fourth request proved the feed had ended. The traversal
        // stops only on an empty page, never on a short page and never on meta.count, so a completed
        // read costs exactly one request more than the number of pages that carried events.
        Assert.Equal(4, client.EventsQueries.Count);
        Assert.Equal(4, batch.PagesFetched);

        // The watermark, and nothing else, moves between requests.
        Assert.Equal(new long[] { 0, 20, 40, 60 }, client.EventsQueries.Select(q => q.AfterSequence));
        Assert.All(client.EventsQueries, query => Assert.Equal(0, query.Page.Offset));

        Assert.Equal(
            new[] { "(seq GT 0)", "(seq GT 20)", "(seq GT 40)", "(seq GT 60)" },
            client.EventsRequestUris.Select(uri => FakeABConnectClient.DecodedParameterValue(uri, "filter[events]")));
        Assert.All(
            client.EventsRequestUris,
            uri => Assert.Equal("0", FakeABConnectClient.ParameterValue(uri, "offset")));

        Assert.Equal(6, batch.Events.Count);
        Assert.Equal(new long[] { 10, 20, 30, 40, 50, 60 }, batch.Events.Select(e => e.Attributes!.Seq));
        Assert.Equal(0, batch.AfterSequence);
        Assert.Equal(60, batch.HighestSequence);
        Assert.Equal(6, batch.ReportedTotalCount);
        Assert.True(batch.IsComplete);
    }

    [Fact]
    public async Task EveryEventRequestCarriesSortEventsSeq()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        ServePages(client, reportedCount: 3, [1, 2], [3]);

        ABConnectFeed feed = CreateFeed(client, options);

        await feed.ReadEventsAsync(0, new EventReadOptions { PageSize = 2 });

        Assert.Equal(3, client.EventsRequestUris.Count);
        Assert.All(client.EventsRequestUris, uri =>
        {
            Assert.Equal("seq", FakeABConnectClient.ParameterValue(uri, "sort[events]"));
            Assert.Contains("sort[events]=seq", uri, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ReadEventPagesAsyncAlsoReAnchorsAndCarriesTheSort()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        ServePages(client, reportedCount: 4, [7, 8], [9, 11]);

        ABConnectFeed feed = CreateFeed(client, options);

        List<EventPage> pages = [];
        await foreach (EventPage page in feed.ReadEventPagesAsync(6, new EventReadOptions { PageSize = 2 }))
        {
            pages.Add(page);
        }

        Assert.Equal(2, pages.Count);
        Assert.Equal(new[] { 1, 2 }, pages.Select(p => p.PageNumber));
        Assert.Equal(new long[] { 8, 11 }, pages.Select(p => p.HighestSequence));
        Assert.All(pages, page => Assert.Equal(4, page.ReportedTotalCount));

        Assert.Equal(new long[] { 6, 8, 11 }, client.EventsQueries.Select(q => q.AfterSequence));
        Assert.All(client.EventsQueries, query => Assert.Equal(0, query.Page.Offset));
        Assert.All(
            client.EventsRequestUris,
            uri => Assert.Equal("seq", FakeABConnectClient.ParameterValue(uri, "sort[events]")));
    }

    [Fact]
    public async Task OutOfOrderSequencesWithinAPageThrowABConnectPagingException()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        ServePages(client, reportedCount: 2, [30, 20]);

        ABConnectFeed feed = CreateFeed(client, options);

        ABConnectPagingException failure = await Assert.ThrowsAsync<ABConnectPagingException>(
            () => feed.ReadEventsAsync(0, new EventReadOptions { PageSize = 2 }));

        // The message has to name both sequences, per section 6.1 guard 1.
        Assert.Contains("20", failure.Message, StringComparison.Ordinal);
        Assert.Contains("30", failure.Message, StringComparison.Ordinal);
        Assert.Contains("sort[events]=seq", failure.Message, StringComparison.Ordinal);

        // The failure happened on the first page, so nothing was retried and nothing was swallowed.
        Assert.Equal(1, client.RequestCount);
    }

    [Fact]
    public async Task ARepeatedSequenceWithinAPageThrowsBecauseAscendingIsStrict()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        ServePages(client, reportedCount: 2, [40, 40]);

        ABConnectFeed feed = CreateFeed(client, options);

        await Assert.ThrowsAsync<ABConnectPagingException>(
            () => feed.ReadEventsAsync(0, new EventReadOptions { PageSize = 2 }));
    }

    [Fact]
    public async Task APageThatDoesNotAdvanceTheMaximumSequenceThrows()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);

        // Requested with (seq GT 100) and answered with sequence 100: ascending, but the walk cannot
        // move, so today's `while (eventSequence >= 0)` loop would request this forever.
        ServePages(client, reportedCount: 1, [100]);

        ABConnectFeed feed = CreateFeed(client, options);

        ABConnectPagingException failure = await Assert.ThrowsAsync<ABConnectPagingException>(
            () => feed.ReadEventsAsync(100, new EventReadOptions { PageSize = 2 }));

        Assert.Contains("cannot advance", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, client.RequestCount);
    }

    [Fact]
    public async Task APageCarryingAnEventAtOrBelowTheWatermarkThrows()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);

        // Ascending, and it does advance, but sequence 5 is at or below the watermark the request
        // carried, so (seq GT 50) was not applied and the page re-delivers an event already seen.
        ServePages(client, reportedCount: 2, [5, 100]);

        ABConnectFeed feed = CreateFeed(client, options);

        ABConnectPagingException failure = await Assert.ThrowsAsync<ABConnectPagingException>(
            () => feed.ReadEventsAsync(50, new EventReadOptions { PageSize = 2 }));

        Assert.Contains("(seq GT 50)", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, client.RequestCount);
    }

    [Fact]
    public async Task AnEventWithNoAttributesThrowsBecauseThereIsNoSafeWatermark()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        client.OnGetEvents = (_, call) => FakeABConnectClient.EventsPage(
            call == 1
                ? new[] { FakeABConnectClient.EventWithoutAttributes("no-attributes") }
                : Array.Empty<ABEvent>(),
            1);

        ABConnectFeed feed = CreateFeed(client, options);

        await Assert.ThrowsAsync<ABConnectPagingException>(() => feed.ReadEventsAsync(0));
    }

    [Fact]
    public async Task AGapInSequenceNumbersNeitherThrowsNorLogsAWarning()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        LogRecorder<ABConnectFeed> logger = new();

        // AB Connect's documentation is explicit that sequence numbers are not incremental, because a
        // partner's feed omits events belonging to other partners. A gap is normal traffic.
        ServePages(client, reportedCount: 3, [1, 5, 9_000_000_000]);

        ABConnectFeed feed = CreateFeed(client, options, logger);

        EventBatch batch = await feed.ReadEventsAsync(0, new EventReadOptions { PageSize = 3 });

        Assert.Equal(3, batch.Events.Count);
        Assert.Equal(9_000_000_000, batch.HighestSequence);
        Assert.True(batch.IsComplete);

        // Nothing at Warning or above was emitted at all, and no line anywhere mentions a gap. This is
        // asserted on captured log output rather than inferred from the absence of an exception.
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Message.Contains("gap", StringComparison.OrdinalIgnoreCase));

        // The recorder reports IsEnabled for every level, so the traversal's own logging did run: the
        // only thing written is the single Debug line per completed traversal. Nothing is logged per
        // page and nothing is logged per gap.
        LogRecorder<ABConnectFeed>.Entry only = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, only.Level);
    }

    [Fact]
    public async Task MaxEventsTruncationSetsIsCompleteFalseAndAsksOnlyForWhatFits()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        ServePages(client, reportedCount: 10, [10, 20], [30, 40]);

        ABConnectFeed feed = CreateFeed(client, options);

        EventBatch batch = await feed.ReadEventsAsync(
            0,
            new EventReadOptions { PageSize = 2, MaxEvents = 3 });

        Assert.Equal(3, batch.Events.Count);
        Assert.Equal(30, batch.HighestSequence);
        Assert.False(batch.IsComplete);
        Assert.Equal(2, batch.PagesFetched);

        // The second request asks for the single row that still fits under the ceiling rather than
        // fetching a full page and discarding half of it.
        Assert.Equal(new[] { 2, 1 }, client.EventsQueries.Select(q => q.Page.Limit));
        Assert.Equal(new[] { "2", "1" }, client.EventsRequestUris.Select(uri => FakeABConnectClient.ParameterValue(uri, "limit")));
    }

    [Fact]
    public async Task MaxEventsTruncationTrimsAPageTheServiceOverfilled()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);

        // The service ignores the limit and returns four rows for a ceiling of three.
        client.OnGetEvents = (_, call) =>
        {
            long[] sequences = call == 1 ? [10, 20, 30, 40] : [];
            return FakeABConnectClient.EventsPage(
                sequences.Select(FakeABConnectClient.EventWithSequence),
                4);
        };

        ABConnectFeed feed = CreateFeed(client, options);

        EventBatch batch = await feed.ReadEventsAsync(0, new EventReadOptions { MaxEvents = 3 });

        Assert.Equal(3, batch.Events.Count);

        // The watermark reported is the last event actually kept, so the discarded fourth event is
        // still past it and will be delivered on the next read.
        Assert.Equal(30, batch.HighestSequence);
        Assert.False(batch.IsComplete);
    }

    [Fact]
    public async Task IsCompleteIsFalseWhenTheCeilingCoincidesWithTheEndOfTheFeedAndTheNextReadSettlesIt()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        ServePages(client, reportedCount: 2, [10, 20]);

        ABConnectFeed feed = CreateFeed(client, options);

        EventBatch first = await feed.ReadEventsAsync(0, new EventReadOptions { PageSize = 2, MaxEvents = 2 });

        // The ceiling stopped the read at the same point the feed happened to end. Claiming
        // completeness there would be a guess, so the read reports itself incomplete.
        Assert.Equal(2, first.Events.Count);
        Assert.False(first.IsComplete);
        Assert.Equal(20, first.HighestSequence);

        EventBatch second = await feed.ReadEventsAsync(first.HighestSequence!.Value);

        Assert.Empty(second.Events);
        Assert.Null(second.HighestSequence);
        Assert.True(second.IsComplete);
    }

    [Fact]
    public async Task ReadEventPagesAsyncYieldsZeroPagesForAnEmptyFeed()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        client.OnGetEvents = (_, _) => FakeABConnectClient.EventsPage([], 0);

        ABConnectFeed feed = CreateFeed(client, options);

        List<EventPage> pages = [];
        await foreach (EventPage page in feed.ReadEventPagesAsync(500))
        {
            pages.Add(page);
        }

        // Zero pages, not one empty page: a yielded page always carries at least one event, so
        // EventPage.HighestSequence is always a real sequence taken from a real event.
        Assert.Empty(pages);
        Assert.Equal(1, client.RequestCount);
        Assert.Equal("(seq GT 500)", FakeABConnectClient.DecodedParameterValue(client.EventsRequestUris[0], "filter[events]"));
    }

    [Fact]
    public async Task AnEmptyFeedIsASuccessfulAnswerAndNotAFailure()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        client.OnGetEvents = (_, _) => FakeABConnectClient.EventsPage([], 0);

        ABConnectFeed feed = CreateFeed(client, options);

        EventBatch batch = await feed.ReadEventsAsync(9_999);

        Assert.Empty(batch.Events);
        Assert.Null(batch.HighestSequence);
        Assert.Equal(0, batch.ReportedTotalCount);
        Assert.Equal(1, batch.PagesFetched);
        Assert.True(batch.IsComplete);
        Assert.Equal(9_999, batch.AfterSequence);
    }

    [Fact]
    public async Task AStandardScopeIsConjoinedWithTheWatermarkOnEveryRequest()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        ServePages(client, reportedCount: 1, [42]);

        ABConnectFeed feed = CreateFeed(client, options);

        await feed.ReadEventsAsync(
            7,
            new EventReadOptions
            {
                PageSize = 2,
                StandardScope = StandardsFilter.ByPublication("9D85340C-B0E5-4C0A-9A1B-2C3D4E5F6A7B"),
            });

        Assert.Equal(
            "((seq GT 7) AND (document.publication.guid EQ '9D85340C-B0E5-4C0A-9A1B-2C3D4E5F6A7B'))",
            FakeABConnectClient.DecodedParameterValue(client.EventsRequestUris[0], "filter[events]"));
        Assert.All(
            client.EventsRequestUris,
            uri => Assert.Equal("seq", FakeABConnectClient.ParameterValue(uri, "sort[events]")));
    }

    [Fact]
    public async Task InvalidEventReadOptionsAreRejectedBeforeAnyRequestIsIssued()
    {
        ABConnectOptions options = new();
        FakeABConnectClient client = new(options);
        ABConnectFeed feed = CreateFeed(client, options);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => feed.ReadEventsAsync(0, new EventReadOptions { PageSize = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => feed.ReadEventsAsync(0, new EventReadOptions { MaxEvents = 0 }));

        // The iterator form validates eagerly too, from the call itself rather than from the first
        // MoveNextAsync, so a bad option cannot masquerade as an empty enumeration.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => { _ = feed.ReadEventPagesAsync(0, new EventReadOptions { PageSize = 0 }); });

        Assert.Equal(0, client.RequestCount);
    }

    /// <summary>
    /// Captures everything written to an <see cref="ILogger{TCategoryName}"/> so a test can assert on
    /// what was and was not logged. Nested and private so it cannot collide with a fake owned by
    /// another test group.
    /// </summary>
    private sealed class LogRecorder<TCategory> : ILogger<TCategory>
    {
        private readonly List<Entry> _entries = [];

        public IReadOnlyList<Entry> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _entries.Add(new Entry(logLevel, eventId, formatter(state, exception)));
        }

        internal sealed record Entry(LogLevel Level, EventId EventId, string Message);

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
