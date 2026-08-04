using System.Net;

namespace OnCourse.ABConnect;

/// <summary>
/// A 5xx status that survived every retry attempt.
/// </summary>
/// <remarks>
/// The retry handler retries all 5xx responses with exponential backoff and full jitter. This
/// exception means the configured attempt budget was spent and the service was still failing;
/// <see cref="ABConnectRequestException.Attempts"/> reports how many attempts that was.
/// </remarks>
public sealed class ABConnectServerException : ABConnectRequestException
{
    /// <summary>
    /// Initializes a new instance describing a failed request.
    /// </summary>
    /// <param name="message">A description of the failure. Must never contain the partner key.</param>
    /// <param name="requestPath">The redacted request path and query.</param>
    /// <param name="statusCode">The HTTP status code of the final attempt, or <see langword="null"/> if no response was received.</param>
    /// <param name="errors">The parsed JSON:API errors, or <see langword="null"/> for none.</param>
    /// <param name="attempts">The number of attempts that were made, counting the initial attempt.</param>
    /// <param name="innerException">The exception that caused this failure, if any.</param>
    public ABConnectServerException(
        string message,
        string requestPath,
        HttpStatusCode? statusCode,
        IReadOnlyList<ABConnectApiError>? errors,
        int attempts,
        Exception? innerException = null)
        : base(message, requestPath, statusCode, errors, attempts, innerException)
    {
    }
}
