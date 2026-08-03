using System.Net;

namespace OnCourse.ABConnect;

/// <summary>
/// The response was received but could not be read as the expected AB Connect envelope.
/// </summary>
/// <remarks>
/// This is raised in preference to returning a default-constructed envelope, because the value-or-throw guarantee
/// requires that a returned value was deserialized from a 2xx response body. The raw body's first
/// 2000 characters appear in <see cref="Exception.Message"/> so the malformed payload is
/// diagnosable without a second request.
/// </remarks>
public sealed class ABConnectResponseFormatException : ABConnectRequestException
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
    public ABConnectResponseFormatException(
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
