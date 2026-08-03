using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OnCourse.ABConnect.Http;
using OnCourse.ABConnect.Throttling;

namespace OnCourse.ABConnect.Handlers;

/// <summary>
/// Acquires one token from the shared client-side bucket before every attempt, including retries.
/// </summary>
/// <remarks>
/// <para>
/// Sits inside the retry handler and outside the signing handler. That ordering is load-bearing: the
/// reverse, which is what a general-purpose transient-error policy gives you, lets a retry burst
/// bypass the bucket and turn a rate-limit problem into a rate-limit storm.
/// </para>
/// <para>
/// On an HTTP 429 this handler does nothing beyond recording the response. It cannot compute a
/// penalty duration, because at the moment it observes the 429 the retry handler that owns backoff
/// timing sits outside it and has not yet run; the penalty is applied by the retry handler instead.
/// </para>
/// <para>
/// Acquisition is asynchronous and the calling thread is never blocked. A request the query builder
/// marked as a wildcard acquires from the narrower wildcard bucket as well as the main one. Nothing
/// this handler logs contains the signed URI: it reports
/// <see cref="ABConnectRequestContext.RedactedPath"/>, which was recorded before any credential was
/// appended.
/// </para>
/// <para>
/// The value-or-throw guarantee reaches down into this handler. For every method on <see cref="IABConnectClient"/> and
/// <see cref="IABConnectFeed"/>: the method either returns a non-null value that was deserialized
/// from a 2xx response body, or it throws. It never returns null, and it never returns a
/// default-constructed envelope. A handler that refused a request without sending it could break that
/// guarantee by synthesizing a plausible-looking empty response, so it does not: when the bucket's
/// queue is full this handler throws <see cref="ABConnectThrottledException"/>, and the caller learns
/// that its request was never asked rather than being told that AB Connect matched nothing.
/// </para>
/// </remarks>
public sealed class ABConnectThrottleHandler : DelegatingHandler
{
    private static readonly Action<ILogger, string, double, int, int, Exception?> LogWaited =
        LoggerMessage.Define<string, double, int, int>(
            LogLevel.Debug,
            new EventId(2001, "ABConnectThrottleWait"),
            "AB Connect throttle waited {WaitMilliseconds:F0} ms for a token before {RequestPath} ({AvailableTokens}/{BucketCapacity} tokens available).");

    private static readonly Action<ILogger, string, long, Exception?> LogThrottled =
        LoggerMessage.Define<string, long>(
            LogLevel.Warning,
            new EventId(2002, "ABConnectThrottleResponse"),
            "AB Connect returned HTTP 429 for {RequestPath}. That is {ThrottleResponseCount} so far, which means the client-side bucket is configured faster than the account's actual rate.");

    private static readonly Action<ILogger, string, int, Exception?> LogQueueFull =
        LoggerMessage.Define<string, int>(
            LogLevel.Warning,
            new EventId(2003, "ABConnectThrottleQueueFull"),
            "AB Connect throttle refused {RequestPath} without sending it: {QueueLimit} acquisitions are already waiting for a token.");

    private readonly IOptions<ABConnectOptions> _options;
    private readonly IABConnectRateLimiterProvider _rateLimiterProvider;
    private readonly ILogger<ABConnectThrottleHandler> _logger;

    /// <summary>Creates the throttle handler.</summary>
    /// <param name="options">The SDK options supplying the throttle settings.</param>
    /// <param name="rateLimiterProvider">The shared, singleton token buckets.</param>
    /// <param name="logger">The logger for wait and 429 diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ABConnectThrottleHandler(
        IOptions<ABConnectOptions> options,
        IABConnectRateLimiterProvider rateLimiterProvider,
        ILogger<ABConnectThrottleHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rateLimiterProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _rateLimiterProvider = rateLimiterProvider;
        _logger = logger;
    }

    /// <summary>Acquires a token, forwards the request, and records a 429 if one comes back.</summary>
    /// <param name="request">The outgoing request.</param>
    /// <param name="cancellationToken">Cancels the wait for a token and the send.</param>
    /// <returns>The response from the inner handler.</returns>
    /// <exception cref="ABConnectThrottledException">
    /// The bucket's queue limit was already reached, so no token can be promised and the request is
    /// refused without being sent.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled while waiting for a token. A cancellation
    /// the caller asked for surfaces unwrapped, per .NET convention.
    /// </exception>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var throttle = _options.Value.Throttle;

        if (throttle is null || !throttle.Enabled)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var context = ABConnectRequestContext.From(request);
        var requestPath = context?.RedactedPath ?? "(unknown request path)";

        var acquisition = await _rateLimiterProvider
            .AcquireAsync(context?.IsWildcardRequest ?? false, cancellationToken)
            .ConfigureAwait(false);

        if (!acquisition.IsAcquired)
        {
            LogQueueFull(_logger, requestPath, throttle.QueueLimit, null);

            throw new ABConnectThrottledException(
                $"The AB Connect client-side rate limit queue is full, so the request to {requestPath} was refused before it was sent. {throttle.QueueLimit} acquisitions are already waiting for a token.",
                requestPath,
                statusCode: null,
                errors: null,
                attempts: context?.Attempts ?? 0,
                retryAfter: acquisition.RetryAfter);
        }

        if (acquisition.Waited > TimeSpan.Zero)
        {
            LogWaited(
                _logger,
                requestPath,
                acquisition.Waited.TotalMilliseconds,
                _rateLimiterProvider.AvailableTokens,
                _rateLimiterProvider.BucketCapacity,
                null);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // Recording only. Draining the bucket is the retry handler's job, because only it knows
            // how long the backoff for this 429 will be.
            _rateLimiterProvider.RecordThrottleResponse();
            LogThrottled(_logger, requestPath, _rateLimiterProvider.ThrottleResponseCount, null);
        }

        return response;
    }

    /// <summary>
    /// Not supported. The AB Connect pipeline is asynchronous throughout.
    /// </summary>
    /// <remarks>
    /// <see cref="DelegatingHandler"/>'s synchronous path does not route through
    /// <see cref="SendAsync"/>, so allowing it would send a request that spent no token. Honoring it
    /// properly would mean blocking the calling thread on the bucket, which this SDK exists to stop
    /// doing.
    /// </remarks>
    /// <param name="request">The outgoing request.</param>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "AB Connect requests must be sent asynchronously. The synchronous send path bypasses client-side throttling, and waiting for a token synchronously would block the calling thread.");
}
