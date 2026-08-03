using System.Net;

namespace OnCourse.ABConnect;

/// <summary>
/// HTTP 429. AB Connect's token bucket for the account was exhausted and every retry attempt was
/// spent still receiving 429.
/// </summary>
/// <remarks>
/// Reaching this exception means the client-side bucket is configured faster than the account's
/// actual rate, since the client-side bucket alone should keep requests inside AB Connect's
/// documented five-tokens-per-second, twenty-five-token-burst limit. The retry handler applies a
/// penalty to the shared limiter before giving up, so subsequent calls are already slowed.
/// </remarks>
public sealed class ABConnectThrottledException : ABConnectRequestException
{
    /// <summary>
    /// Initializes a new instance describing an exhausted rate limit.
    /// </summary>
    /// <param name="message">A description of the failure. Must never contain the partner key.</param>
    /// <param name="requestPath">The redacted request path and query.</param>
    /// <param name="statusCode">The HTTP status code of the final attempt, normally 429.</param>
    /// <param name="errors">The parsed JSON:API errors, or <see langword="null"/> for none.</param>
    /// <param name="attempts">The number of attempts that were made, counting the initial attempt.</param>
    /// <param name="retryAfter">The delay advertised by the <c>Retry-After</c> header, if the final response carried one.</param>
    /// <param name="innerException">The exception that caused this failure, if any.</param>
    public ABConnectThrottledException(
        string message,
        string requestPath,
        HttpStatusCode? statusCode,
        IReadOnlyList<ABConnectApiError>? errors,
        int attempts,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base(message, requestPath, statusCode, errors, attempts, innerException)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// The delay advertised by the final response's <c>Retry-After</c> header, in either the
    /// delta-seconds or the HTTP-date form, or <see langword="null"/> when the header was absent.
    /// AB Connect does not document this header, so callers must treat it as optional.
    /// </summary>
    public TimeSpan? RetryAfter { get; }
}
