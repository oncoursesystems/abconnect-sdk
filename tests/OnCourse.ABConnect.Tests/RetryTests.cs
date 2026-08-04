using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OnCourse.ABConnect.Handlers;
using OnCourse.ABConnect.Models;
using OnCourse.ABConnect.Queries;
using OnCourse.ABConnect.Tests.Fakes;
using OnCourse.ABConnect.Throttling;
using Xunit;

namespace OnCourse.ABConnect.Tests;

/// <summary>
/// Section 9 "Retry" test group, locking down defect 13 and acceptance criterion AC-5.
/// </summary>
/// <remarks>
/// <para>
/// Every test drives the real pipeline in the order section 4 requires, retry then throttle then
/// signing, and reaches it through the real <see cref="ABConnectClient"/> wherever the assertion is
/// about the exception a caller sees, so that the attempt count and the exception type are the ones
/// production produces rather than ones a handler test invented.
/// </para>
/// <para>
/// Time is <see cref="TransportVirtualClock"/> throughout: a backoff of thirty seconds costs the test
/// no real time at all, and the duration the handler asked to wait is asserted exactly, from
/// <see cref="TransportVirtualClock.ScheduledDelays"/>, rather than inferred from a stopwatch. Jitter
/// is made deterministic by the constructor overload that takes a <see cref="Random"/>.
/// </para>
/// <para>
/// Client-side throttling is switched off in this group. The token bucket has its own group, and
/// leaving it on here would mix bucket waits into the delays under assertion.
/// </para>
/// </remarks>
public sealed class RetryTests
{
    private const string EmptyEventsPage =
        """{"data":[],"meta":{"limit":100,"offset":0,"count":0,"took":1},"links":{"self":"events"}}""";

    private const string ServerErrorBody =
        """{"errors":[{"status":"500","title":"Server Error","detail":"An unexpected error occurred."}]}""";

    private const string ThrottledBody =
        """{"errors":[{"status":"429","title":"Too Many Requests"}]}""";

    [Fact]
    public async Task A429ThenAnOkYieldsOneRetryAndASuccessfulResult()
    {
        TransportVirtualClock clock = new();
        using FakeHttpMessageHandler fake = new();
        fake.Enqueue(Throttled(retryAfterSeconds: null));
        fake.EnqueueJson(HttpStatusCode.OK, EmptyEventsPage);

        using ClientHarness harness = new(NewOptions(), clock, fake);

        ABPage<ABEvent> page = await harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 0 });

        Assert.Equal(2, fake.SendCount);
        Assert.Empty(page.Data);
        Assert.Equal(0, page.Meta.Count);
        Assert.Single(clock.ScheduledDelays);
    }

    [Fact]
    public async Task RetryAfterThreeSecondsProducesAThreeSecondWait()
    {
        TransportVirtualClock clock = new();
        using FakeHttpMessageHandler fake = new();
        fake.Enqueue(Throttled(retryAfterSeconds: 3));
        fake.EnqueueJson(HttpStatusCode.OK, EmptyEventsPage);

        using ClientHarness harness = new(NewOptions(), clock, fake);

        await harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 0 });

        // The header wins over the jitter, and it is honored to the second.
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(clock.ScheduledDelays));
        Assert.Equal(TimeSpan.FromSeconds(3), clock.Elapsed);
    }

    [Fact]
    public async Task RetryAfterAsAnUnvalidatedHeaderStringIsHonored()
    {
        TransportVirtualClock clock = new();
        using FakeHttpMessageHandler fake = new();

        HttpResponseMessage throttled = Throttled(retryAfterSeconds: null);
        throttled.Headers.TryAddWithoutValidation("Retry-After", "3");
        fake.Enqueue(throttled);
        fake.EnqueueJson(HttpStatusCode.OK, EmptyEventsPage);

        using ClientHarness harness = new(NewOptions(), clock, fake);

        await harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 0 });

        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(clock.ScheduledDelays));
    }

    [Fact]
    public async Task RetryAfterInTheHttpDateFormIsParsed()
    {
        TransportVirtualClock clock = new();
        DateTimeOffset resumeAt = clock.GetUtcNow().AddSeconds(7);

        using FakeHttpMessageHandler fake = new();
        HttpResponseMessage throttled = Throttled(retryAfterSeconds: null);
        throttled.Headers.RetryAfter = new RetryConditionHeaderValue(resumeAt);
        fake.Enqueue(throttled);
        fake.EnqueueJson(HttpStatusCode.OK, EmptyEventsPage);

        using ClientHarness harness = new(NewOptions(), clock, fake);

        await harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 0 });

        // An HTTP-date is resolved against the injected clock, so the wait is the remaining interval.
        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(clock.ScheduledDelays));
        Assert.Equal(resumeAt, clock.GetUtcNow());
    }

    [Fact]
    public async Task RetryAfterAsAnUnvalidatedHttpDateStringIsParsed()
    {
        TransportVirtualClock clock = new();
        DateTimeOffset resumeAt = clock.GetUtcNow().AddSeconds(9);

        using FakeHttpMessageHandler fake = new();
        HttpResponseMessage throttled = Throttled(retryAfterSeconds: null);
        throttled.Headers.TryAddWithoutValidation(
            "Retry-After",
            resumeAt.ToString("r", System.Globalization.CultureInfo.InvariantCulture));
        fake.Enqueue(throttled);
        fake.EnqueueJson(HttpStatusCode.OK, EmptyEventsPage);

        using ClientHarness harness = new(NewOptions(), clock, fake);

        await harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 0 });

        Assert.Equal(TimeSpan.FromSeconds(9), Assert.Single(clock.ScheduledDelays));
    }

    [Fact]
    public async Task RetryAfterIsIgnoredWhenHonorRetryAfterIsOff()
    {
        TransportVirtualClock clock = new();
        using FakeHttpMessageHandler fake = new();
        fake.Enqueue(Throttled(retryAfterSeconds: 3));
        fake.EnqueueJson(HttpStatusCode.OK, EmptyEventsPage);

        ABConnectOptions options = NewOptions(o => o.Retry.HonorRetryAfter = false);
        using ClientHarness harness = new(options, clock, fake, new FixedJitter(1.0));

        await harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 0 });

        // The computed backoff for the first retry, not the three seconds the header advertised.
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(clock.ScheduledDelays));
    }

    [Fact]
    public async Task FiveConsecutive500sThrowAServerExceptionWithFiveAttempts()
    {
        TransportVirtualClock clock = new();
        using FakeHttpMessageHandler fake = new();
        fake.EnqueueJsonRepeated(5, HttpStatusCode.InternalServerError, ServerErrorBody);

        using ClientHarness harness = new(NewOptions(), clock, fake);

        ABConnectServerException failure = await Assert.ThrowsAsync<ABConnectServerException>(
            () => harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 0 }));

        Assert.Equal(5, fake.SendCount);
        Assert.Equal(5, failure.Attempts);
        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        Assert.Equal("Server Error", Assert.Single(failure.Errors).Title);

        // Four backoffs for five attempts, and nothing in the report names a credential.
        Assert.Equal(4, clock.ScheduledDelays.Count);
        AssertNoCredentials(failure.RequestPath);
        AssertNoCredentials(failure.ToString());
    }

    [Fact]
    public async Task FiveConsecutive429sThrowAThrottledExceptionCarryingRetryAfter()
    {
        TransportVirtualClock clock = new();
        using FakeHttpMessageHandler fake = new();

        for (int index = 0; index < 5; index++)
        {
            fake.Enqueue(Throttled(retryAfterSeconds: 7));
        }

        using ClientHarness harness = new(NewOptions(), clock, fake);

        ABConnectThrottledException failure = await Assert.ThrowsAsync<ABConnectThrottledException>(
            () => harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 0 }));

        Assert.Equal(5, fake.SendCount);
        Assert.Equal(5, failure.Attempts);
        Assert.Equal(TimeSpan.FromSeconds(7), failure.RetryAfter);
        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.Equal([7, 7, 7, 7], clock.ScheduledDelays.Select(delay => delay.TotalSeconds));
    }

    [Fact]
    public async Task A403ProducesZeroRetriesAndNoBackoff()
    {
        TransportVirtualClock clock = new();
        using FakeHttpMessageHandler fake = new();
        fake.EnqueueJson(
            HttpStatusCode.Forbidden,
            """{"errors":[{"status":"403","title":"Not Licensed"}]}""");

        using ClientHarness harness = new(NewOptions(), clock, fake);

        ABConnectNotLicensedException failure = await Assert.ThrowsAsync<ABConnectNotLicensedException>(
            () => harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 0 }));

        Assert.Equal(1, fake.SendCount);
        Assert.Equal(1, failure.Attempts);
        Assert.Empty(clock.ScheduledDelays);
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, typeof(ABConnectInvalidRequestException))]
    [InlineData(HttpStatusCode.Unauthorized, typeof(ABConnectAuthenticationException))]
    [InlineData(HttpStatusCode.Forbidden, typeof(ABConnectNotLicensedException))]
    [InlineData(HttpStatusCode.NotFound, typeof(ABConnectNotFoundException))]
    [InlineData(HttpStatusCode.Gone, typeof(ABConnectInvalidRequestException))]
    public async Task TerminalStatusCodesAreNeverRetried(HttpStatusCode statusCode, Type expected)
    {
        TransportVirtualClock clock = new();
        using FakeHttpMessageHandler fake = new();
        fake.EnqueueJson(statusCode, """{"errors":[{"title":"Terminal"}]}""");

        using ClientHarness harness = new(NewOptions(), clock, fake);

        ABConnectRequestException failure = await Assert.ThrowsAnyAsync<ABConnectRequestException>(
            () => harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 0 }));

        Assert.IsType(expected, failure);
        Assert.Equal(1, fake.SendCount);
        Assert.Empty(clock.ScheduledDelays);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task RetryableStatusCodesUseEveryConfiguredAttempt(HttpStatusCode statusCode)
    {
        TransportVirtualClock clock = new();
        using FakeHttpMessageHandler fake = new();
        fake.EnqueueJsonRepeated(5, statusCode, """{"errors":[{"title":"Retryable"}]}""");

        using ClientHarness harness = new(NewOptions(), clock, fake);

        ABConnectRequestException failure = await Assert.ThrowsAnyAsync<ABConnectRequestException>(
            () => harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 0 }));

        Assert.Equal(5, fake.SendCount);
        Assert.Equal(5, failure.Attempts);
    }

    [Fact]
    public async Task BackoffIsExponentialAndBoundedByMaxDelay()
    {
        TransportVirtualClock clock = new();
        using FakeHttpMessageHandler fake = new();
        fake.EnqueueJsonRepeated(5, HttpStatusCode.InternalServerError, ServerErrorBody);

        // Deliberately small numbers: the uncapped ladder would be 2, 4, 8, 16 seconds, all of which
        // this clock still fast-forwards through, so an implementation that forgot the cap fails this
        // test rather than hanging in it.
        ABConnectOptions options = NewOptions(o =>
        {
            o.Retry.BaseDelay = TimeSpan.FromSeconds(2);
            o.Retry.MaxDelay = TimeSpan.FromSeconds(5);
        });

        // The jitter fraction is pinned at its supremum, so each delay is the cap itself and the cap is
        // what is under assertion.
        using ClientHarness harness = new(options, clock, fake, new FixedJitter(1.0));

        await Assert.ThrowsAsync<ABConnectServerException>(
            () => harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 0 }));

        Assert.Equal(
            [
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
            ],
            clock.ScheduledDelays);

        foreach (TimeSpan delay in clock.ScheduledDelays)
        {
            Assert.True(delay <= TimeSpan.FromSeconds(5), $"A backoff of {delay} exceeded MaxDelay.");
        }
    }

    [Fact]
    public async Task BackoffScalesWithTheJitterFraction()
    {
        TransportVirtualClock clock = new();
        using FakeHttpMessageHandler fake = new();
        fake.EnqueueJsonRepeated(5, HttpStatusCode.InternalServerError, ServerErrorBody);

        using ClientHarness harness = new(NewOptions(), clock, fake, new FixedJitter(0.5));

        await Assert.ThrowsAsync<ABConnectServerException>(
            () => harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 0 }));

        // Half of the exponential cap at each attempt: full jitter is uniform over [0, cap], so the
        // delay is the fraction times the cap and not the cap plus a wobble.
        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(4),
            ],
            clock.ScheduledDelays);
    }

    [Fact]
    public async Task FullJitterCanChooseNoDelayAtAll()
    {
        TransportVirtualClock clock = new();
        using FakeHttpMessageHandler fake = new();
        fake.EnqueueJsonRepeated(5, HttpStatusCode.InternalServerError, ServerErrorBody);

        using ClientHarness harness = new(NewOptions(), clock, fake, new FixedJitter(0.0));

        await Assert.ThrowsAsync<ABConnectServerException>(
            () => harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 0 }));

        Assert.Equal(5, fake.SendCount);
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
    }

    [Fact]
    public async Task MaxAttemptsOfOneMeansTheRequestIsSentOnce()
    {
        TransportVirtualClock clock = new();
        using FakeHttpMessageHandler fake = new();
        fake.EnqueueJson(HttpStatusCode.InternalServerError, ServerErrorBody);

        ABConnectOptions options = NewOptions(o => o.Retry.MaxAttempts = 1);
        using ClientHarness harness = new(options, clock, fake);

        ABConnectServerException failure = await Assert.ThrowsAsync<ABConnectServerException>(
            () => harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 0 }));

        Assert.Equal(1, fake.SendCount);
        Assert.Equal(1, failure.Attempts);
        Assert.Empty(clock.ScheduledDelays);
    }

    [Fact]
    public async Task CallerCancellationSurfacesOperationCanceledExceptionAndNotAnABConnectException()
    {
        TransportVirtualClock clock = new();
        using FakeHttpMessageHandler fake = new();
        fake.EnqueueJson(HttpStatusCode.OK, EmptyEventsPage);

        using ClientHarness harness = new(NewOptions(), clock, fake);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 0 }, cancelled.Token));

        Assert.IsNotAssignableFrom<ABConnectException>(failure);
    }

    [Fact]
    public async Task CancellationDuringTheBackoffIsNotWrappedEither()
    {
        TransportVirtualClock clock = new();
        using CancellationTokenSource cancelled = new();
        using FakeHttpMessageHandler fake = new();

        // The caller gives up while the first backoff is pending, which is the case a retry loop is
        // most likely to get wrong.
        fake.Responder = _ =>
        {
            cancelled.Cancel();
            return Response(HttpStatusCode.InternalServerError, ServerErrorBody);
        };

        using ClientHarness harness = new(NewOptions(), clock, fake);

        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 0 }, cancelled.Token));

        Assert.IsNotAssignableFrom<ABConnectException>(failure);
        Assert.Equal(1, fake.SendCount);
    }

    [Fact]
    public async Task TransportFailuresAreRetriedAndTheLastOneSurfacesAsATransportException()
    {
        TransportVirtualClock clock = new();
        using FakeHttpMessageHandler fake = new();

        for (int index = 0; index < 5; index++)
        {
            fake.EnqueueFailure(new HttpRequestException("Name or service not known."));
        }

        using ClientHarness harness = new(NewOptions(), clock, fake);

        ABConnectTransportException failure = await Assert.ThrowsAsync<ABConnectTransportException>(
            () => harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 0 }));

        Assert.Equal(5, fake.SendCount);
        Assert.Equal(5, failure.Attempts);
        Assert.Null(failure.StatusCode);
        Assert.IsType<HttpRequestException>(failure.InnerException);
        Assert.Equal(4, clock.ScheduledDelays.Count);
    }

    [Fact]
    public async Task ATransportFailureFollowedByASuccessIsRetried()
    {
        TransportVirtualClock clock = new();
        using FakeHttpMessageHandler fake = new();
        fake.EnqueueFailure(new HttpRequestException("Connection reset by peer."));
        fake.EnqueueJson(HttpStatusCode.OK, EmptyEventsPage);

        using ClientHarness harness = new(NewOptions(), clock, fake);

        ABPage<ABEvent> page = await harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 0 });

        Assert.Equal(2, fake.SendCount);
        Assert.Empty(page.Data);
    }

    [Fact]
    public async Task EveryRetryIsLoggedAtWarningUnderTheStableEventIdWithNoCredentials()
    {
        TransportVirtualClock clock = new();
        using FakeHttpMessageHandler fake = new();
        fake.EnqueueJsonRepeated(3, HttpStatusCode.InternalServerError, ServerErrorBody);
        fake.EnqueueJson(HttpStatusCode.OK, EmptyEventsPage);

        RetryLogSink sink = new();
        using ClientHarness harness = new(NewOptions(), clock, fake, retryLogger: sink);

        await harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 0 });

        Assert.Equal(3, sink.Entries.Count);

        foreach ((LogLevel level, EventId eventId, string message) in sink.Entries)
        {
            Assert.Equal(LogLevel.Warning, level);
            Assert.Equal(ABConnectRetryHandler.RetryScheduledEventId, eventId);
            Assert.Contains("events", message, StringComparison.Ordinal);
            AssertNoCredentials(message);
        }
    }

    [Fact]
    public async Task EveryAttemptIsSignedExactlyOnce()
    {
        // The retry handler restores the request URI before each attempt, so the signing handler cannot
        // append a second authentication fragment on the way through.
        TransportVirtualClock clock = new();
        using FakeHttpMessageHandler fake = new();
        fake.EnqueueJsonRepeated(2, HttpStatusCode.InternalServerError, ServerErrorBody);
        fake.EnqueueJson(HttpStatusCode.OK, EmptyEventsPage);

        using ClientHarness harness = new(NewOptions(), clock, fake);

        await harness.Client.GetEventsAsync(new EventsQuery { AfterSequence = 0 });

        Assert.Equal(3, fake.RequestUris.Count);

        foreach (string uri in fake.RequestUris)
        {
            Assert.Equal(1, Occurrences(uri, "partner.id="));
            Assert.Equal(1, Occurrences(uri, "auth.signature="));
            Assert.Equal(1, Occurrences(uri, "auth.expires="));
        }
    }

    private static ABConnectOptions NewOptions(Action<ABConnectOptions>? configure = null)
    {
        ABConnectOptions options = new()
        {
            BaseAddress = new Uri("https://api.abconnect.instructure.com/rest/v4.1/"),
            PartnerId = "test_account",
            PartnerKey = "ajk84Hjk93h59skaAJ8732",
        };

        options.Throttle.Enabled = false;

        configure?.Invoke(options);
        return options;
    }

    private static void AssertNoCredentials(string text)
    {
        Assert.DoesNotContain("ajk84Hjk93h59skaAJ8732", text, StringComparison.Ordinal);
        Assert.DoesNotContain("auth.signature=", text, StringComparison.Ordinal);
        Assert.DoesNotContain("partner.id=", text, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string body) =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Throttled(int? retryAfterSeconds)
    {
        HttpResponseMessage response = Response(HttpStatusCode.TooManyRequests, ThrottledBody);

        if (retryAfterSeconds is { } seconds)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        }

        return response;
    }

    private static int Occurrences(string text, string value)
    {
        int count = 0;

        for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// The real <see cref="ABConnectClient"/> over the real three-handler pipeline over a
    /// <see cref="FakeHttpMessageHandler"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpClient.Timeout"/> is infinite, as the dependency-injection registration sets it,
    /// because <see cref="ABConnectOptions.RequestTimeout"/> is enforced per attempt inside the retry
    /// handler. A finite value here would be a per-logical-call budget and would kill a retried call
    /// mid-ladder. It also keeps the real clock out of the test entirely.
    /// </remarks>
    private sealed class ClientHarness : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ABConnectRateLimiterProvider _provider;

        public ClientHarness(
            ABConnectOptions options,
            TransportVirtualClock clock,
            HttpMessageHandler transport,
            Random? jitter = null,
            ILogger<ABConnectRetryHandler>? retryLogger = null)
        {
            IOptions<ABConnectOptions> wrapped = Options.Create(options);
            _provider = new ABConnectRateLimiterProvider(wrapped, clock);

            ABConnectRetryHandler retry = new(
                wrapped,
                _provider,
                clock,
                retryLogger ?? NullLogger<ABConnectRetryHandler>.Instance,
                jitter ?? new FixedJitter(1.0))
            {
                InnerHandler = new ABConnectThrottleHandler(
                    wrapped,
                    _provider,
                    NullLogger<ABConnectThrottleHandler>.Instance)
                {
                    InnerHandler = new ABConnectSigningHandler(wrapped, clock)
                    {
                        InnerHandler = transport,
                    },
                },
            };

            _httpClient = new HttpClient(retry, disposeHandler: true)
            {
                BaseAddress = options.BaseAddress,
                Timeout = Timeout.InfiniteTimeSpan,
            };

            Client = new ABConnectClient(
                _httpClient,
                wrapped,
                _provider,
                NullLogger<ABConnectClient>.Instance);
        }

        public ABConnectClient Client { get; }

        public void Dispose()
        {
            _httpClient.Dispose();
            _provider.Dispose();
        }
    }

    /// <summary>
    /// A <see cref="Random"/> whose fraction is fixed, so a full-jitter delay is exactly predictable.
    /// </summary>
    /// <remarks>
    /// A fraction of 1.0 is the supremum of the uniform distribution the handler draws from, so it
    /// makes the delay equal to the exponential cap and lets the cap be asserted directly. A fraction
    /// of 0.0 is the infimum, which is the "no delay at all" end of full jitter.
    /// </remarks>
    private sealed class FixedJitter : Random
    {
        private readonly double _fraction;

        public FixedJitter(double fraction) => _fraction = fraction;

        public override double NextDouble() => _fraction;
    }

    /// <summary>Records the retry handler's log entries so their level and event id can be asserted.</summary>
    private sealed class RetryLogSink : ILogger<ABConnectRetryHandler>
    {
        private readonly List<(LogLevel Level, EventId EventId, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, EventId EventId, string Message)> Entries => _entries;

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => new Scope();

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _entries.Add((logLevel, eventId, formatter(state, exception)));
        }

        private sealed class Scope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
