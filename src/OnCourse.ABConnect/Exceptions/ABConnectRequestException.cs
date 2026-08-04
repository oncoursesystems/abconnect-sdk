using System.Net;

namespace OnCourse.ABConnect;

/// <summary>
/// A request was made and did not produce a usable result. Every derived type identifies one
/// documented AB Connect outcome, so a caller branches by catching the type it cares about rather
/// than by switching over a status code.
/// </summary>
public abstract class ABConnectRequestException : ABConnectException
{
    /// <summary>
    /// Initializes a new instance describing a failed request.
    /// </summary>
    /// <param name="message">
    /// A description of the failure. When the response body could not be parsed as a JSON:API error
    /// document, the mapper places the body's first 2000 characters here. Must never contain the
    /// partner key.
    /// </param>
    /// <param name="requestPath">
    /// The redacted request path and query. See <see cref="RequestPath"/>.
    /// </param>
    /// <param name="statusCode">The HTTP status code of the final attempt, or <see langword="null"/> if no response was received.</param>
    /// <param name="errors">The parsed JSON:API errors, or <see langword="null"/> for none.</param>
    /// <param name="attempts">The number of attempts that were made, counting the initial attempt.</param>
    /// <param name="innerException">The exception that caused this failure, if any.</param>
    protected ABConnectRequestException(
        string message,
        string requestPath,
        HttpStatusCode? statusCode,
        IReadOnlyList<ABConnectApiError>? errors,
        int attempts,
        Exception? innerException = null)
        : base(message, innerException)
    {
        RequestPath = requestPath;
        StatusCode = statusCode;
        Errors = errors ?? [];
        Attempts = attempts;
    }

    /// <summary>
    /// The request path and query with credentials redacted. The signing handler records the
    /// pre-signature path on the request's options and the response mapper uses that, so the
    /// partner key never appears in an exception, a log line, or a message.
    /// </summary>
    public string RequestPath { get; }

    /// <summary>
    /// The HTTP status code of the final attempt, or <see langword="null"/> when the failure
    /// occurred before any response was received, as for a DNS, TLS, socket, or timeout failure.
    /// </summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// The parsed JSON:API <c>errors[]</c> entries from the response body. Never null; empty when
    /// the body was absent or was not parseable as an AB Connect error document, in which case the
    /// raw body's leading characters appear in <see cref="Exception.Message"/> instead.
    /// </summary>
    public IReadOnlyList<ABConnectApiError> Errors { get; }

    /// <summary>
    /// How many attempts were made before the failure was reported, counting the initial attempt.
    /// A value of 1 means the failure was terminal and no retry was appropriate.
    /// </summary>
    public int Attempts { get; }
}
