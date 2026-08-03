using System.Net;

namespace OnCourse.ABConnect;

/// <summary>
/// The request never produced an HTTP response: a DNS failure, a TLS failure, a socket failure, or
/// a per-attempt timeout.
/// </summary>
/// <remarks>
/// <see cref="ABConnectRequestException.StatusCode"/> is always <see langword="null"/> for this
/// type. A per-attempt timeout surfaces here with the originating
/// <see cref="TaskCanceledException"/> as the inner exception. A cancellation the caller requested
/// is not wrapped: it surfaces as <see cref="OperationCanceledException"/>, per .NET convention.
/// </remarks>
public sealed class ABConnectTransportException : ABConnectRequestException
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
    public ABConnectTransportException(
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
