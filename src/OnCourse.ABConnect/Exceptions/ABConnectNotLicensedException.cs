using System.Net;

namespace OnCourse.ABConnect;

/// <summary>
/// HTTP 403. A syntactically valid GUID that falls outside the account's license.
/// </summary>
/// <remarks>
/// AB Connect documents that "If it is a valid GUID but you aren't licensed for it, the API will
/// respond with a 403". The vendor does not document whether a 403 for an unlicensed GUID is
/// distinguishable from a 403 for a bad signature, so this exception carries the parsed
/// <see cref="ABConnectRequestException.Errors"/> list and lets the caller inspect the detail text.
/// During event processing this is a documented normal outcome that a caller may legitimately skip.
/// This status is terminal for a GET and is never retried.
/// </remarks>
public sealed class ABConnectNotLicensedException : ABConnectRequestException
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
    public ABConnectNotLicensedException(
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
