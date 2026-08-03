using System.Globalization;
using System.Net;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OnCourse.ABConnect.Http;
using OnCourse.ABConnect.Throttling;

namespace OnCourse.ABConnect.Handlers;

/// <summary>
/// Owns the attempt loop: retries HTTP 429, any 5xx, and transport-level failures with exponential
/// backoff and full jitter.
/// </summary>
/// <remarks>
/// <para>
/// Outermost of the three handlers, so that every retry attempt passes back through the throttle
/// handler and spends a token.
/// </para>
/// <para>
/// HTTP 400, 401, 403, and 404 are terminal for a GET and are never retried. Attempt <c>n</c> waits a
/// uniformly random duration in <c>[0, min(MaxDelay, BaseDelay * 2^(n-1))]</c>; full jitter rather
/// than a fixed ladder matters because a resumed pull retries many requests at once and a fixed
/// ladder synchronizes them into a new burst.
/// </para>
/// <para>
/// After computing the backoff for a 429, this handler calls
/// <see cref="IABConnectRateLimiterProvider.ApplyPenalty"/>, which is the self-adjusting behavior AB
/// Connect asks clients to implement. When attempts are exhausted nothing is swallowed: the failing
/// response flows on to the response mapper, which throws.
/// </para>
/// <para>
/// A cancellation the caller requested is never retried and never wrapped: it surfaces as
/// <see cref="OperationCanceledException"/>, per .NET convention. A
/// <see cref="TaskCanceledException"/> raised while the caller's token is still uncancelled is a
/// per-attempt timeout, not a cancellation, and is retried.
/// </para>
/// </remarks>
public sealed class ABConnectRetryHandler : DelegatingHandler
{
    private const int RetryScheduledEventIdValue = 3001;
    private const string RetryScheduledEventName = "ABConnectRetryScheduled";

    /// <summary>
    /// Stands in for the redacted path of a request that did not come from this SDK's query builder
    /// and so carries no <see cref="ABConnectRequestContext"/>.
    /// </summary>
    private const string UnknownRequestPath = "(no AB Connect request context)";

    /// <summary>
    /// Logs a retry scheduled after a response arrived. <c>StatusCode</c> is a structured field so a
    /// consumer can filter on it without parsing the message.
    /// </summary>
    private static readonly Action<ILogger, int, int, int, double, string, Exception?> LogStatusRetryScheduled =
        LoggerMessage.Define<int, int, int, double, string>(
            LogLevel.Warning,
            new EventId(RetryScheduledEventIdValue, RetryScheduledEventName),
            "AB Connect attempt {Attempt} of {MaxAttempts} failed with {StatusCode}; retrying after " +
            "{DelayMilliseconds} ms. Request: {RequestPath}");

    /// <summary>
    /// Logs a retry scheduled after a transport failure, where there is no status code to report and
    /// the failure's type name is the only log-safe description of it.
    /// </summary>
    private static readonly Action<ILogger, int, int, string, double, string, Exception?> LogTransportRetryScheduled =
        LoggerMessage.Define<int, int, string, double, string>(
            LogLevel.Warning,
            new EventId(RetryScheduledEventIdValue, RetryScheduledEventName),
            "AB Connect attempt {Attempt} of {MaxAttempts} failed with {Outcome}; retrying after " +
            "{DelayMilliseconds} ms. Request: {RequestPath}");

    private readonly IOptions<ABConnectOptions> _options;
    private readonly IABConnectRateLimiterProvider _rateLimiterProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ABConnectRetryHandler> _logger;
    private readonly Random _random;

    /// <summary>Creates the retry handler.</summary>
    /// <param name="options">The SDK options supplying the retry settings.</param>
    /// <param name="rateLimiterProvider">The shared token buckets, penalized after a 429.</param>
    /// <param name="timeProvider">The clock used to delay between attempts, so tests need not sleep.</param>
    /// <param name="logger">The logger every retry is reported to, with the request path redacted.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    [ActivatorUtilitiesConstructor]
    public ABConnectRetryHandler(
        IOptions<ABConnectOptions> options,
        IABConnectRateLimiterProvider rateLimiterProvider,
        TimeProvider timeProvider,
        ILogger<ABConnectRetryHandler> logger)
        : this(options, rateLimiterProvider, timeProvider, logger, Random.Shared)
    {
    }

    /// <summary>
    /// Creates the retry handler with an explicit source for the full-jitter random fraction.
    /// </summary>
    /// <remarks>
    /// The dependency-injection registration uses the four-argument constructor, which supplies
    /// <see cref="Random.Shared"/> and is marked
    /// <see cref="ActivatorUtilitiesConstructorAttribute"/> so the container's choice cannot become
    /// ambiguous. This overload exists so a test can make jitter deterministic by passing a
    /// <see cref="Random"/> whose
    /// <see cref="Random.NextDouble"/> is overridden or seeded, and so assert the exact delay a given
    /// attempt number produces.
    /// </remarks>
    /// <param name="options">The SDK options supplying the retry settings.</param>
    /// <param name="rateLimiterProvider">The shared token buckets, penalized after a 429.</param>
    /// <param name="timeProvider">The clock used to delay between attempts, so tests need not sleep.</param>
    /// <param name="logger">The logger every retry is reported to, with the request path redacted.</param>
    /// <param name="random">
    /// The source of the jitter fraction. Each retry consumes one <see cref="Random.NextDouble"/>
    /// value in <c>[0, 1)</c> and multiplies the capped backoff by it.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ABConnectRetryHandler(
        IOptions<ABConnectOptions> options,
        IABConnectRateLimiterProvider rateLimiterProvider,
        TimeProvider timeProvider,
        ILogger<ABConnectRetryHandler> logger,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rateLimiterProvider);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(random);

        _options = options;
        _rateLimiterProvider = rateLimiterProvider;
        _timeProvider = timeProvider;
        _logger = logger;
        _random = random;
    }

    /// <summary>
    /// The event id every retry is logged under, at <see cref="LogLevel.Warning"/>. Stable across
    /// releases so a consumer can filter or assert on it.
    /// </summary>
    public static EventId RetryScheduledEventId { get; } =
        new(RetryScheduledEventIdValue, RetryScheduledEventName);

    /// <summary>Runs the attempt loop.</summary>
    /// <remarks>
    /// <para>
    /// Returns the last response even when it failed, because the response mapper is the single place
    /// a non-2xx becomes an exception. Transport failures cannot be represented as a response, so the
    /// last one is rethrown with its original stack trace for the caller to wrap.
    /// </para>
    /// <para>
    /// <see cref="ABConnectOptions.RequestTimeout"/> is applied here, per attempt rather than per
    /// logical call, so a retried call may legitimately take longer in total. The timeout cancels only
    /// the attempt it belongs to and surfaces as a retryable failure; the caller's own token, when it
    /// is the one that was cancelled, propagates unwrapped instead.
    /// </para>
    /// <para>
    /// The request URI is restored before every retry, so a handler further in that appends
    /// credentials to the query string cannot append them twice on the second attempt.
    /// </para>
    /// </remarks>
    /// <param name="request">The outgoing request.</param>
    /// <param name="cancellationToken">Cancels the backoff delays and the sends.</param>
    /// <returns>The final response, successful or not.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        ABConnectOptions options = _options.Value;
        ABConnectRetryOptions retry = options.Retry;
        int maxAttempts = Math.Max(1, retry.MaxAttempts);
        Uri? originalRequestUri = request.RequestUri;

        // A request that did not come from this SDK's query builder carries no context. It still
        // needs an attempt counter, so it gets a detached one; it simply has no redacted path to
        // report. The context attached to the request, when there is one, is the one the mapper
        // reads, so the counter it sees is the real one.
        ABConnectRequestContext context =
            ABConnectRequestContext.From(request) ?? new ABConnectRequestContext(UnknownRequestPath);

        for (int attempt = 1; ; attempt++)
        {
            context.Attempts = attempt;
            if (attempt > 1)
            {
                request.RequestUri = originalRequestUri;
            }

            HttpResponseMessage? response = null;
            ExceptionDispatchInfo? transportFailure = null;

            using (CancellationTokenSource? attemptTimeout = CreateAttemptTimeout(options.RequestTimeout))
            using (CancellationTokenSource? attemptSource = attemptTimeout is null
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attemptTimeout.Token))
            {
                try
                {
                    response = await base
                        .SendAsync(request, attemptSource?.Token ?? cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The caller asked to stop. Never retried, never wrapped.
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    // The caller's token is not cancelled, so this is a per-attempt timeout, which
                    // this SDK treats as a transport failure rather than as a cancellation.
                    transportFailure = ExceptionDispatchInfo.Capture(ex);
                }
                catch (HttpRequestException ex)
                {
                    transportFailure = ExceptionDispatchInfo.Capture(ex);
                }
            }

            bool isFinalAttempt = attempt >= maxAttempts;

            if (transportFailure is null)
            {
                if (!IsRetryable(response!.StatusCode) || isFinalAttempt)
                {
                    return response;
                }
            }
            else if (isFinalAttempt)
            {
                transportFailure.Throw();
            }

            TimeSpan? retryAfter = transportFailure is null && retry.HonorRetryAfter
                ? ReadRetryAfter(response!)
                : null;
            TimeSpan delay = ComputeDelay(attempt, retry, retryAfter);

            if (transportFailure is null && response!.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Section 4.2 puts the penalty here and nowhere else: the throttle handler sits
                // inside this one and cannot know the backoff duration when it sees the 429.
                _rateLimiterProvider.ApplyPenalty(delay);
            }

            LogRetryScheduled(attempt, maxAttempts, response, transportFailure, delay, context.RedactedPath);

            response?.Dispose();

            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates the source that cancels a single attempt once
    /// <see cref="ABConnectOptions.RequestTimeout"/> elapses on the injected clock, or null when no
    /// finite timeout is configured. The registration must leave
    /// <see cref="HttpClient.Timeout"/> infinite so that this per-attempt budget, and not a
    /// whole-call budget, is what bounds an attempt.
    /// </summary>
    private CancellationTokenSource? CreateAttemptTimeout(TimeSpan requestTimeout)
        => requestTimeout > TimeSpan.Zero && requestTimeout != Timeout.InfiniteTimeSpan
            ? new CancellationTokenSource(requestTimeout, _timeProvider)
            : null;

    /// <summary>
    /// Whether a status code is worth another attempt: HTTP 429 and any 5xx. HTTP 400, 401, 403, and
    /// 404 are terminal for a GET, and so is every other 4xx.
    /// </summary>
    private static bool IsRetryable(HttpStatusCode statusCode)
    {
        int code = (int)statusCode;
        return statusCode == HttpStatusCode.TooManyRequests || code is >= 500 and < 600;
    }

    /// <summary>
    /// Reads the <c>Retry-After</c> header in either documented form, delta-seconds or HTTP-date.
    /// AB Connect does not document sending this header at all, so an absent or unparseable value is
    /// simply ignored.
    /// </summary>
    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } typed)
        {
            if (typed.Delta is { } delta)
            {
                return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
            }

            if (typed.Date is { } date)
            {
                TimeSpan until = date - _timeProvider.GetUtcNow();
                return until > TimeSpan.Zero ? until : TimeSpan.Zero;
            }
        }

        // A fake or non-validating handler may have added the header as an unparsed string.
        if (!response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? raw))
        {
            return null;
        }

        string? value = raw.FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
        {
            return TimeSpan.FromSeconds(Math.Max(0, seconds));
        }

        if (DateTimeOffset.TryParseExact(
                value,
                "r",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset httpDate))
        {
            TimeSpan until = httpDate - _timeProvider.GetUtcNow();
            return until > TimeSpan.Zero ? until : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// Computes how long to wait before attempt <c>n + 1</c>. An advertised <c>Retry-After</c> wins,
    /// because a duration the service stated is better information than a guess; otherwise the delay
    /// is exponential with full jitter, uniformly random in
    /// <c>[0, min(MaxDelay, BaseDelay * 2^(n-1))]</c>.
    /// </summary>
    private TimeSpan ComputeDelay(int attempt, ABConnectRetryOptions retry, TimeSpan? retryAfter)
    {
        if (retryAfter is { } advertised)
        {
            return advertised;
        }

        double baseTicks = Math.Max(0d, retry.BaseDelay.Ticks);
        double maxTicks = Math.Max(0d, retry.MaxDelay.Ticks);
        double capTicks = Math.Min(baseTicks * Math.Pow(2, attempt - 1), maxTicks);

        return capTicks <= 0d ? TimeSpan.Zero : TimeSpan.FromTicks((long)(_random.NextDouble() * capTicks));
    }

    /// <summary>
    /// Reports one scheduled retry at <see cref="LogLevel.Warning"/> under
    /// <see cref="RetryScheduledEventId"/>.
    /// </summary>
    /// <remarks>
    /// What went wrong is described either by the status code that came back or, for a transport
    /// failure, by the failure's type name. The response body is never logged, because AB Connect
    /// echoes the request query back in <c>links.self</c> and the body is therefore not credential-free
    /// the way <see cref="ABConnectRequestContext.RedactedPath"/> is. The path logged is always the
    /// redacted one, so no log line can contain the minted signature or the partner key.
    /// </remarks>
    private void LogRetryScheduled(
        int attempt,
        int maxAttempts,
        HttpResponseMessage? response,
        ExceptionDispatchInfo? transportFailure,
        TimeSpan delay,
        string redactedPath)
    {
        if (transportFailure is not null)
        {
            LogTransportRetryScheduled(
                _logger,
                attempt,
                maxAttempts,
                transportFailure.SourceException.GetType().Name,
                delay.TotalMilliseconds,
                redactedPath,
                transportFailure.SourceException);
            return;
        }

        LogStatusRetryScheduled(
            _logger,
            attempt,
            maxAttempts,
            (int)response!.StatusCode,
            delay.TotalMilliseconds,
            redactedPath,
            null);
    }
}
