using System.Net;

namespace OnCourse.ABConnect;

/// <summary>
/// HTTP 401. The credentials were rejected, or the account's license does not cover the call
/// that was made.
/// </summary>
/// <remarks>
/// AB Connect documents both meanings for 401: "If you attempt to access a feature or piece of data
/// and receive a 401 error, check your credentials and the details of the error message body. It may
/// be that your current license does not support the call you are making." Inspect
/// <see cref="ABConnectRequestException.Errors"/> to tell the two apart. This status is terminal for
/// a GET and is never retried.
/// </remarks>
public sealed class ABConnectAuthenticationException : ABConnectRequestException
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
    public ABConnectAuthenticationException(
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
