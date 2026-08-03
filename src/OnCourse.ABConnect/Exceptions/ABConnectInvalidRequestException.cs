using System.Net;

namespace OnCourse.ABConnect;

/// <summary>
/// A 4xx status other than 401, 403, 404, or 429. The request itself was malformed or was rejected
/// on its merits.
/// </summary>
/// <remarks>
/// Typically a filter, field, or sort expression AB Connect would not accept; when the service says
/// which one, <see cref="ABConnectRequestException.Errors"/> carries it in
/// <see cref="ABConnectApiError.SourceParameter"/>. This status is terminal for a GET and is never
/// retried.
/// </remarks>
public sealed class ABConnectInvalidRequestException : ABConnectRequestException
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
    public ABConnectInvalidRequestException(
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
