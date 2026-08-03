using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OnCourse.ABConnect.Handlers;
using OnCourse.ABConnect.Http;
using OnCourse.ABConnect.Tests.Fakes;
using OnCourse.ABConnect.Throttling;
using Xunit;

namespace OnCourse.ABConnect.Tests;

/// <summary>
/// The throttling policy this SDK owns, and only that.
/// </summary>
/// <remarks>
/// <para>
/// The bucket arithmetic belongs to <c>System.Threading.RateLimiting.TokenBucketRateLimiter</c> and is
/// not tested here. Whether a token bucket replenishes correctly, paces a burst, or serves waiters in
/// order is the framework's contract to keep and Microsoft's suite to prove; re-asserting it here would
/// only test that a dependency still behaves the way it did when the assertion was written.
/// </para>
/// <para>
/// What these tests do cover is everything AB Connect specific layered on top, all of which is this
/// SDK's own code and none of which the framework has an opinion about: that a wildcard request is
/// charged to two buckets rather than one, that a post-429 penalty drains the burst allowance and is
/// applied by the retry handler alone, that a refusal becomes a typed exception instead of a
/// plausible-looking empty answer, that every attempt including retries spends its own token, that
/// disabling the throttle really bypasses it, that the configured capacity reaches the limiter, and
/// that one registration means one shared bucket.
/// </para>
/// <para>
/// Where a test needs the bucket to be empty it drains it by acquiring, and where it needs a refusal it
/// sets <c>QueueLimit</c> to zero so the refusal is immediate. Nothing here waits on a real clock for a
/// token to replenish, so the whole group runs in milliseconds.
/// </para>
/// </remarks>
public sealed class ThrottlingTests
{
    private const string EmptyPage =
        """{"data":[],"meta":{"limit":100,"offset":0,"count":0,"took":1},"links":{"self":"events"}}""";

    [Fact]
    public async Task TheConfiguredCapacityAndRateReachTheLimiter()
    {
        TransportVirtualClock clock = new();
        ABConnectOptions options = NewOptions(o =>
        {
            o.Throttle.BucketCapacity = 7;
            o.Throttle.QueueLimit = 0;
        });

        using ABConnectRateLimiterProvider provider = new(Options.Create(options), clock);

        Assert.Equal(7, provider.BucketCapacity);
        Assert.Equal(7, provider.AvailableTokens);

        await DrainAsync(provider, 7);

        Assert.Equal(0, provider.AvailableTokens);
        Assert.Equal(7L, provider.AcquiredCount);

        // The eighth is past the configured burst, and with no queue it is refused rather than held.
        ABConnectRateLimitAcquisition eighth = await provider.AcquireAsync(false);

        Assert.False(eighth.IsAcquired);
        Assert.Equal(7L, provider.AcquiredCount);
    }

    [Fact]
    public async Task AnInvalidThrottleConfigurationFailsAsConfigurationRatherThanAsAnArgument()
    {
        TransportVirtualClock clock = new();
        ABConnectOptions options = NewOptions(o => o.Throttle.TokensPerSecond = 0);

        ABConnectConfigurationException failure = Assert.Throws<ABConnectConfigurationException>(
            () => new ABConnectRateLimiterProvider(Options.Create(options), clock));

        Assert.Contains(ABConnectOptions.SectionName, failure.Message, StringComparison.Ordinal);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task ThrottleHandlerAloneNeverDrainsTheBucketOnA429()
    {
        // At the moment the throttle handler observes a 429 the retry handler sits outside it and has
        // computed no delay, so the handler records the response and does nothing else. The bucket must
        // be down by exactly the one token this attempt spent.
        TransportVirtualClock clock = new();
        ABConnectOptions options = NewOptions();
        using PenaltySpy spy = new(new ABConnectRateLimiterProvider(Options.Create(options), clock));

        using FakeHttpMessageHandler fake = new();
        fake.Enqueue(Response429(retryAfterSeconds: null));

        using HttpMessageInvoker invoker = new(NewThrottleHandler(options, spy, fake));

        using HttpResponseMessage response = await SendAsync(invoker, options, "events?limit=100");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Empty(spy.Penalties);
        Assert.Equal(1L, spy.ThrottleResponseCount);
        Assert.Equal(24, spy.AvailableTokens);
    }

    [Fact]
    public async Task A429DrainsTheBucketThroughTheRetryHandlersPenaltyAndNothingElse()
    {
        TransportVirtualClock clock = new();

        // A deliberately fast replenishment so the retried attempt does not spend real time waiting for
        // a token. The rate is irrelevant to what this test asserts, which is who drains the bucket.
        ABConnectOptions options = NewOptions(o => o.Throttle.TokensPerSecond = 1000);
        using PenaltySpy spy = new(new ABConnectRateLimiterProvider(Options.Create(options), clock));

        using FakeHttpMessageHandler fake = new();
        fake.Enqueue(Response429(retryAfterSeconds: 3));
        fake.EnqueueJson(HttpStatusCode.OK, EmptyPage);

        using HttpMessageInvoker invoker = new(BuildPipeline(options, spy, clock, fake));

        using HttpResponseMessage response = await SendAsync(invoker, options, "events?limit=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, fake.SendCount);

        // Exactly one penalty, applied by the retry handler, for exactly the delay it was about to
        // wait, and it emptied the burst allowance.
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(spy.Penalties));
        Assert.Equal(0, Assert.Single(spy.TokensAfterPenalty));
        Assert.Equal(1L, spy.ThrottleResponseCount);

        // Both attempts spent a token of their own.
        Assert.Equal(2L, spy.AcquiredCount);
    }

    [Fact]
    public async Task APenaltyDrainsTheBurstAndHoldsTheNextRequestForItsWholeDuration()
    {
        TransportVirtualClock clock = new();
        ABConnectOptions options = NewOptions(o => o.Throttle.TokensPerSecond = 1000);
        using ABConnectRateLimiterProvider provider = new(Options.Create(options), clock);

        provider.ApplyPenalty(TimeSpan.FromSeconds(30));

        // The standing burst is spent, so the penalty cannot be ridden out on tokens already banked.
        Assert.Equal(0, provider.AvailableTokens);

        ABConnectRateLimitAcquisition acquisition = await provider.AcquireAsync(false);

        Assert.True(acquisition.IsAcquired);
        Assert.Equal(1L, provider.AcquiredCount);

        // The wait was served on the injected clock, for exactly the penalty, once.
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(clock.ScheduledDelays));
        Assert.Equal(TimeSpan.FromSeconds(30), clock.Elapsed);
        Assert.Equal(1L, provider.WaitCount);
    }

    [Fact]
    public async Task AShorterPenaltyNeverCutsShortOneAlreadyInForce()
    {
        // Two 429s in flight together must not let the second one's shorter backoff release the first
        // one's penalty early.
        TransportVirtualClock clock = new();
        ABConnectOptions options = NewOptions(o => o.Throttle.TokensPerSecond = 1000);
        using ABConnectRateLimiterProvider provider = new(Options.Create(options), clock);

        provider.ApplyPenalty(TimeSpan.FromSeconds(30));
        provider.ApplyPenalty(TimeSpan.FromSeconds(1));

        Assert.True((await provider.AcquireAsync(false)).IsAcquired);

        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(clock.ScheduledDelays));
    }

    [Fact]
    public async Task AnExpiredPenaltyStopsCostingAnythingAtAll()
    {
        TransportVirtualClock clock = new();
        ABConnectOptions options = NewOptions(o => o.Throttle.TokensPerSecond = 1000);
        using ABConnectRateLimiterProvider provider = new(Options.Create(options), clock);

        provider.ApplyPenalty(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.True((await provider.AcquireAsync(false)).IsAcquired);

        // No delay was scheduled, because the penalty had already run out before the request arrived.
        Assert.Empty(clock.ScheduledDelays);
        Assert.Equal(0L, provider.WaitCount);
    }

    [Fact]
    public async Task RetriesAcquireTheirOwnTokens()
    {
        TransportVirtualClock clock = new();
        ABConnectOptions options = NewOptions();
        using PenaltySpy spy = new(new ABConnectRateLimiterProvider(Options.Create(options), clock));

        using FakeHttpMessageHandler fake = new();
        fake.EnqueueJsonRepeated(
            2,
            HttpStatusCode.InternalServerError,
            """{"errors":[{"title":"Server Error"}]}""");
        fake.EnqueueJson(HttpStatusCode.OK, EmptyPage);

        using HttpMessageInvoker invoker = new(BuildPipeline(options, spy, clock, fake));

        using HttpResponseMessage response = await SendAsync(invoker, options, "events?limit=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, fake.SendCount);
        Assert.Equal(3L, spy.AcquiredCount);
        Assert.Equal(22, spy.AvailableTokens);

        // A 5xx is not a rate-limit signal, so no penalty is applied for one.
        Assert.Empty(spy.Penalties);
    }

    [Fact]
    public async Task AWildcardRequestIsChargedToBothBuckets()
    {
        TransportVirtualClock clock = new();

        // No queue, so the moment either bucket cannot pay the request is refused instead of held. That
        // turns "which bucket ran out" into an immediate, timing-free observation.
        ABConnectOptions options = NewOptions(o =>
        {
            o.Throttle.QueueLimit = 0;
            o.Throttle.WildcardBucketCapacity = 2;
        });

        using ABConnectRateLimiterProvider provider = new(Options.Create(options), clock);

        // The narrow bucket holds two, so two discovery calls succeed and each also spends from the main
        // bucket: 25 down to 23.
        for (int index = 0; index < 2; index++)
        {
            Assert.True((await provider.AcquireAsync(true)).IsAcquired);
        }

        Assert.Equal(23, provider.AvailableTokens);

        // The third is refused by the narrow bucket even though the main bucket still holds 23, which is
        // only possible if the wildcard bucket is being charged as well.
        Assert.False((await provider.AcquireAsync(true)).IsAcquired);

        // An ordinary request is not gated by the narrow bucket at all.
        Assert.True((await provider.AcquireAsync(false)).IsAcquired);
        Assert.Equal(22, provider.AvailableTokens);
    }

    [Fact]
    public async Task TheThrottleHandlerPassesTheWildcardMarkFromTheRequestContext()
    {
        TransportVirtualClock clock = new();
        ABConnectOptions options = NewOptions();
        using PenaltySpy spy = new(new ABConnectRateLimiterProvider(Options.Create(options), clock));

        using FakeHttpMessageHandler fake = new();
        fake.EnqueueJsonRepeated(2, HttpStatusCode.OK, EmptyPage);

        using HttpMessageInvoker invoker = new(NewThrottleHandler(options, spy, fake));

        using HttpResponseMessage discovery =
            await SendAsync(invoker, options, "standards?fields[standards]=*", isWildcard: true);
        using HttpResponseMessage ordinary = await SendAsync(invoker, options, "standards?limit=100");

        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ordinary.StatusCode);
        Assert.Equal([true, false], spy.WildcardFlags);
    }

    [Fact]
    public async Task ThrottlingDisabledSpendsNoTokensAtAll()
    {
        TransportVirtualClock clock = new();
        ABConnectOptions options = NewOptions(o => o.Throttle.Enabled = false);
        using PenaltySpy spy = new(new ABConnectRateLimiterProvider(Options.Create(options), clock));

        using FakeHttpMessageHandler fake = new();
        fake.EnqueueJsonRepeated(100, HttpStatusCode.OK, EmptyPage);

        using HttpMessageInvoker invoker = new(NewThrottleHandler(options, spy, fake));

        for (int index = 0; index < 100; index++)
        {
            using HttpResponseMessage response = await SendAsync(invoker, options, "events?limit=100");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(100, fake.SendCount);
        Assert.Empty(spy.WildcardFlags);
        Assert.Equal(0L, spy.AcquiredCount);
        Assert.Equal(25, spy.AvailableTokens);
    }

    [Fact]
    public async Task AFullQueueRefusesTheRequestWithoutSendingIt()
    {
        // The value-or-throw guarantee reaches into the throttle handler: a request that was never asked
        // must not come back looking like an answer of nothing.
        TransportVirtualClock clock = new();
        ABConnectOptions options = NewOptions(o => o.Throttle.QueueLimit = 0);
        using ABConnectRateLimiterProvider provider = new(Options.Create(options), clock);

        await DrainAsync(provider, 25);

        using FakeHttpMessageHandler fake = new();

        using HttpMessageInvoker invoker = new(NewThrottleHandler(options, provider, fake));

        ABConnectThrottledException failure = await Assert.ThrowsAsync<ABConnectThrottledException>(
            () => SendAsync(invoker, options, "events?limit=100"));

        Assert.Equal(0, fake.SendCount);
        Assert.Equal("events?limit=100", failure.RequestPath);
        Assert.Null(failure.StatusCode);
        Assert.Empty(failure.Errors);

        // Whatever estimate the limiter offers for when a token next arrives is surfaced to the caller
        // rather than dropped.
        Assert.NotNull(failure.RetryAfter);
        Assert.True(failure.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public async Task TwoPipelinesOverOneProviderShareOneBucket()
    {
        // AB Connect's bucket is per account, so two typed clients built over one registration must not
        // each get a burst of 25. Twelve requests each plus one more is 25, and the 26th is refused.
        TransportVirtualClock clock = new();
        ABConnectOptions options = NewOptions(o => o.Throttle.QueueLimit = 0);
        using ABConnectRateLimiterProvider provider = new(Options.Create(options), clock);

        using FakeHttpMessageHandler firstTransport = new();
        using FakeHttpMessageHandler secondTransport = new();
        firstTransport.EnqueueJsonRepeated(13, HttpStatusCode.OK, EmptyPage);
        secondTransport.EnqueueJsonRepeated(13, HttpStatusCode.OK, EmptyPage);

        using HttpMessageInvoker first = new(NewThrottleHandler(options, provider, firstTransport));
        using HttpMessageInvoker second = new(NewThrottleHandler(options, provider, secondTransport));

        for (int index = 0; index < 12; index++)
        {
            using HttpResponseMessage fromFirst = await SendAsync(first, options, "events?limit=100");
            using HttpResponseMessage fromSecond = await SendAsync(second, options, "standards?limit=100");
        }

        using HttpResponseMessage twentyFifth = await SendAsync(first, options, "events?limit=100");

        Assert.Equal(HttpStatusCode.OK, twentyFifth.StatusCode);
        Assert.Equal(25L, provider.AcquiredCount);
        Assert.Equal(0, provider.AvailableTokens);

        // The 26th finds a bucket the other pipeline helped empty.
        await Assert.ThrowsAsync<ABConnectThrottledException>(
            () => SendAsync(second, options, "standards?limit=100"));
    }

    private static ABConnectOptions NewOptions(Action<ABConnectOptions>? configure = null)
    {
        ABConnectOptions options = new()
        {
            BaseAddress = new Uri("https://api.abconnect.instructure.com/rest/v4.1/"),
            PartnerId = "test_account",
            PartnerKey = "ajk84Hjk93h59skaAJ8732",
        };

        configure?.Invoke(options);
        return options;
    }

    private static async Task DrainAsync(IABConnectRateLimiterProvider provider, int count)
    {
        for (int index = 0; index < count; index++)
        {
            ABConnectRateLimitAcquisition acquisition =
                await provider.AcquireAsync(false).ConfigureAwait(false);

            Assert.True(acquisition.IsAcquired);
        }
    }

    /// <summary>The throttle handler alone, with no retry handler above it.</summary>
    private static ABConnectThrottleHandler NewThrottleHandler(
        ABConnectOptions options,
        IABConnectRateLimiterProvider provider,
        HttpMessageHandler transport) =>
        new(Options.Create(options), provider, NullLogger<ABConnectThrottleHandler>.Instance)
        {
            InnerHandler = transport,
        };

    /// <summary>
    /// The full pipeline in the order section 4 requires, outermost first: retry, then throttle, then
    /// signing, then the fake transport.
    /// </summary>
    private static ABConnectRetryHandler BuildPipeline(
        ABConnectOptions options,
        IABConnectRateLimiterProvider provider,
        TransportVirtualClock clock,
        HttpMessageHandler transport) =>
        new(
            Options.Create(options),
            provider,
            clock,
            NullLogger<ABConnectRetryHandler>.Instance)
        {
            InnerHandler = new ABConnectThrottleHandler(
                Options.Create(options),
                provider,
                NullLogger<ABConnectThrottleHandler>.Instance)
            {
                InnerHandler = new ABConnectSigningHandler(Options.Create(options), clock)
                {
                    InnerHandler = transport,
                },
            },
        };

    private static async Task<HttpResponseMessage> SendAsync(
        HttpMessageInvoker invoker,
        ABConnectOptions options,
        string relativeUri,
        bool isWildcard = false)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(options.BaseAddress, relativeUri));
        new ABConnectRequestContext(relativeUri) { IsWildcardRequest = isWildcard }.AttachTo(request);

        return await invoker.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
    }

    private static HttpResponseMessage Response429(int? retryAfterSeconds)
    {
        HttpResponseMessage response = new(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(
                """{"errors":[{"status":"429","title":"Too Many Requests"}]}""",
                Encoding.UTF8,
                "application/json"),
        };

        if (retryAfterSeconds is { } seconds)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        }

        return response;
    }

    /// <summary>
    /// Wraps the real provider so a test can prove <em>who</em> drained the bucket, not merely that it
    /// was drained.
    /// </summary>
    /// <remarks>
    /// <see cref="TokensAfterPenalty"/> is sampled inside the call, before the retry handler's backoff
    /// has had a chance to let tokens accrue again, so it observes the drain itself rather than its
    /// aftermath.
    /// </remarks>
    private sealed class PenaltySpy : IABConnectRateLimiterProvider
    {
        private readonly IABConnectRateLimiterProvider _inner;
        private readonly List<TimeSpan> _penalties = [];
        private readonly List<int> _tokensAfterPenalty = [];
        private readonly List<bool> _wildcardFlags = [];

        public PenaltySpy(IABConnectRateLimiterProvider inner) => _inner = inner;

        public IReadOnlyList<TimeSpan> Penalties => _penalties;

        public IReadOnlyList<int> TokensAfterPenalty => _tokensAfterPenalty;

        public IReadOnlyList<bool> WildcardFlags => _wildcardFlags;

        public int AvailableTokens => _inner.AvailableTokens;

        public int BucketCapacity => _inner.BucketCapacity;

        public long AcquiredCount => _inner.AcquiredCount;

        public long WaitCount => _inner.WaitCount;

        public TimeSpan TotalWaitTime => _inner.TotalWaitTime;

        public long ThrottleResponseCount => _inner.ThrottleResponseCount;

        public ValueTask<ABConnectRateLimitAcquisition> AcquireAsync(
            bool isWildcardRequest,
            CancellationToken cancellationToken = default)
        {
            _wildcardFlags.Add(isWildcardRequest);
            return _inner.AcquireAsync(isWildcardRequest, cancellationToken);
        }

        public void ApplyPenalty(TimeSpan duration)
        {
            _inner.ApplyPenalty(duration);
            _penalties.Add(duration);
            _tokensAfterPenalty.Add(_inner.AvailableTokens);
        }

        public void RecordThrottleResponse() => _inner.RecordThrottleResponse();

        public void Dispose() => _inner.Dispose();
    }
}

/// <summary>
/// A <see cref="TimeProvider"/> for the transport test groups: virtual time that advances only when
/// the code under test asks to wait.
/// </summary>
/// <remarks>
/// <para>
/// It wraps a <see cref="FakeTimeProvider"/> and, whenever a timer is created for a delay at or below
/// <see cref="AutoAdvanceLimit"/>, records the requested duration and advances virtual time by exactly
/// that much. So a <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> inside a
/// handler completes without any real waiting and without a test having to
/// guess when to advance, and the exact duration the production code asked for is recoverable from
/// <see cref="ScheduledDelays"/>. Nothing here is a real-clock delay, a sleep, or a poll.
/// </para>
/// <para>
/// The limit exists to keep the per-attempt timeout out of the way: a backoff is at most
/// <see cref="ABConnectRetryOptions.MaxDelay"/>, 30 seconds by default, while
/// <see cref="ABConnectOptions.RequestTimeout"/> is 100 seconds, so a timer for the timeout is created
/// as production creates it and simply never comes due.
/// </para>
/// <para>
/// The consequence is that a delay longer than the limit is left pending, which would hang whatever is
/// waiting on it. A test that asserts on a delay must therefore keep its <em>worst case</em> delay
/// inside the window, including the worst case a broken implementation would produce, so that the
/// failure mode is a failed assertion rather than a hung test.
/// </para>
/// <para>
/// Shared deliberately by <see cref="ThrottlingTests"/>, <see cref="RetryTests"/>, and any later
/// transport test, so there is one place where "virtual time" is defined.
/// </para>
/// </remarks>
internal sealed class TransportVirtualClock : TimeProvider
{
    private readonly FakeTimeProvider _inner;
    private readonly DateTimeOffset _start;
    private readonly object _gate = new();
    private readonly List<TimeSpan> _scheduled = [];

    public TransportVirtualClock()
        : this(new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero))
    {
    }

    public TransportVirtualClock(DateTimeOffset start)
    {
        _inner = new FakeTimeProvider(start);
        _start = start;
    }

    /// <summary>The longest delay this clock fast-forwards through. Longer timers are left pending.</summary>
    public TimeSpan AutoAdvanceLimit { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Virtual time consumed since construction.</summary>
    public TimeSpan Elapsed => _inner.GetUtcNow() - _start;

    /// <summary>Every delay the code under test asked to wait, in the order it asked.</summary>
    public IReadOnlyList<TimeSpan> ScheduledDelays
    {
        get
        {
            lock (_gate)
            {
                return [.. _scheduled];
            }
        }
    }

    /// <summary>Advances virtual time explicitly, for a test that is driving the clock itself.</summary>
    public void Advance(TimeSpan delta) => _inner.Advance(delta);

    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

    public override long GetTimestamp() => _inner.GetTimestamp();

    public override long TimestampFrequency => _inner.TimestampFrequency;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ITimer timer = _inner.CreateTimer(callback, state, dueTime, period);

        if (dueTime > TimeSpan.Zero && dueTime <= AutoAdvanceLimit)
        {
            lock (_gate)
            {
                _scheduled.Add(dueTime);
            }

            _inner.Advance(dueTime);
        }

        return timer;
    }
}
