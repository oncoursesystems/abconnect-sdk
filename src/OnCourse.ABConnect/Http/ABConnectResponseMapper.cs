using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OnCourse.ABConnect.Http;

/// <summary>
/// Turns an AB Connect HTTP response into either a deserialized value or the right exception.
/// </summary>
/// <remarks>
/// <para>
/// This is the single place the value-or-throw guarantee is enforced: for every method on
/// <see cref="IABConnectClient"/> and <see cref="IABConnectFeed"/>, the method either returns a
/// non-null value that was deserialized from a 2xx response body, or it throws. It never returns
/// null, and it never returns a default-constructed envelope.
/// </para>
/// <para>
/// It is also the single place a non-2xx status becomes an exception. The status mapping is HTTP 401
/// to <see cref="ABConnectAuthenticationException"/>, 403 to
/// <see cref="ABConnectNotLicensedException"/>, 404 to <see cref="ABConnectNotFoundException"/>, 429
/// to <see cref="ABConnectThrottledException"/>, any other 4xx to
/// <see cref="ABConnectInvalidRequestException"/>, any 5xx to
/// <see cref="ABConnectServerException"/>, a transport or timeout failure to
/// <see cref="ABConnectTransportException"/>, and an unreadable body to
/// <see cref="ABConnectResponseFormatException"/>. A cancellation the caller requested is not mapped
/// at all: it surfaces as <see cref="OperationCanceledException"/>, per .NET convention.
/// </para>
/// </remarks>
public static class ABConnectResponseMapper
{
    /// <summary>
    /// The maximum number of characters of an unparseable response body that are copied into an
    /// exception message.
    /// </summary>
    public const int MaxBodyExcerptLength = 2000;

    /// <summary>
    /// Matches the credential-bearing query parameters, so that a body excerpt echoing the request
    /// query back (AB Connect's <c>links.self</c> does exactly that) cannot leak the partner key or
    /// the minted signature into an exception message.
    /// </summary>
    private static readonly Regex CredentialPattern = new(
        @"(auth\.signature|partner\.id|partner\.key)=[^&""'\s\\]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads a successful response body, or throws the exception that the response's status maps to.
    /// </summary>
    /// <typeparam name="T">The type to deserialize a 2xx body into.</typeparam>
    /// <param name="response">The response to read. Ownership stays with the caller.</param>
    /// <param name="context">The request context recorded before credentials were appended.</param>
    /// <param name="serializerOptions">The serializer options to deserialize with.</param>
    /// <param name="cancellationToken">Cancels reading the body.</param>
    /// <returns>The deserialized body. Never null.</returns>
    /// <remarks>
    /// This method is where the value-or-throw guarantee becomes true in practice: the method either returns a
    /// non-null value that was deserialized from a 2xx response body, or it throws. It never returns
    /// null, and it never returns a default-constructed envelope. A body of JSON <c>null</c>, and a
    /// body missing a member the envelope requires, are both format failures rather than an empty
    /// result, so that "AB Connect matched nothing" and "AB Connect did not answer" can never be
    /// confused.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="response"/>, <paramref name="context"/>, or <paramref name="serializerOptions"/> is null.</exception>
    /// <exception cref="ABConnectRequestException">The response was not successful, or a 2xx body could not be read as <typeparamref name="T"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        ABConnectRequestContext context,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(serializerOptions);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response, context, cancellationToken).ConfigureAwait(false);
        }

        byte[] body;
        try
        {
            body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception cause) when (cause is HttpRequestException or IOException or TaskCanceledException)
        {
            throw CreateTransportException(cause, context);
        }

        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(body, serializerOptions);
        }
        catch (JsonException cause)
        {
            throw new ABConnectResponseFormatException(
                BuildFormatMessage(response, context, body, cause.Message),
                context.RedactedPath,
                response.StatusCode,
                [],
                AttemptsOf(context),
                cause);
        }

        return value ?? throw new ABConnectResponseFormatException(
            BuildFormatMessage(response, context, body, "the body deserialized to null"),
            context.RedactedPath,
            response.StatusCode,
            [],
            AttemptsOf(context));
    }

    /// <summary>
    /// Builds the exception an unsuccessful response maps to, without throwing it.
    /// </summary>
    /// <param name="response">The unsuccessful response.</param>
    /// <param name="context">The request context recorded before credentials were appended.</param>
    /// <param name="cancellationToken">Cancels reading the error body.</param>
    /// <returns>The exception that describes the failure.</returns>
    /// <remarks>
    /// The returned exception always carries the redacted request path, the status code, the parsed
    /// JSON:API errors, and the attempt count the retry handler recorded. When the body does not
    /// parse as a JSON:API error document the error list is empty and the body's first
    /// <see cref="MaxBodyExcerptLength"/> characters go into the message instead, with any
    /// credential-bearing query parameter the body echoed back replaced by a redaction marker.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> or <paramref name="context"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<ABConnectRequestException> CreateExceptionAsync(
        HttpResponseMessage response,
        ABConnectRequestContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(context);

        string? body = null;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception cause) when (cause is HttpRequestException or IOException or TaskCanceledException)
        {
            // A failure body that cannot even be read must not mask the status code, which is the
            // more useful diagnosis. Fall through with no body.
        }

        IReadOnlyList<ABConnectApiError> errors = ParseErrors(body);
        int attempts = AttemptsOf(context);
        string message = BuildFailureMessage(response, context, body, errors, attempts);
        HttpStatusCode status = response.StatusCode;

        return status switch
        {
            HttpStatusCode.Unauthorized => new ABConnectAuthenticationException(
                message, context.RedactedPath, status, errors, attempts),
            HttpStatusCode.Forbidden => new ABConnectNotLicensedException(
                message, context.RedactedPath, status, errors, attempts),
            HttpStatusCode.NotFound => new ABConnectNotFoundException(
                message, context.RedactedPath, status, errors, attempts),
            HttpStatusCode.TooManyRequests => new ABConnectThrottledException(
                message, context.RedactedPath, status, errors, attempts, ReadRetryAfter(response)),
            _ when (int)status is >= 500 and < 600 => new ABConnectServerException(
                message, context.RedactedPath, status, errors, attempts),
            _ => new ABConnectInvalidRequestException(
                message, context.RedactedPath, status, errors, attempts),
        };
    }

    /// <summary>
    /// Builds the exception a transport-level failure maps to.
    /// </summary>
    /// <param name="cause">The DNS, TLS, socket, or timeout failure that occurred.</param>
    /// <param name="context">The request context recorded before credentials were appended.</param>
    /// <returns>A transport exception carrying <paramref name="cause"/> as its inner exception.</returns>
    /// <remarks>
    /// A cancellation the caller requested must never reach this method. By design, caller
    /// cancellation surfaces as a bare <see cref="OperationCanceledException"/> and is
    /// never wrapped in an AB Connect type; only a per-attempt timeout, which is a
    /// <see cref="TaskCanceledException"/> raised while the caller's token is still uncancelled,
    /// belongs here.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="cause"/> or <paramref name="context"/> is null.</exception>
    public static ABConnectTransportException CreateTransportException(
        Exception cause,
        ABConnectRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(cause);
        ArgumentNullException.ThrowIfNull(context);

        int attempts = AttemptsOf(context);
        bool isTimeout = cause is TaskCanceledException;
        string what = isTimeout ? "timed out" : "failed before any response was received";

        string message = string.Format(
            CultureInfo.InvariantCulture,
            "The AB Connect request to {0} {1} after {2} attempt(s): {3}",
            context.RedactedPath,
            what,
            attempts,
            Redact(cause.Message));

        return new ABConnectTransportException(
            message,
            context.RedactedPath,
            statusCode: null,
            errors: null,
            attempts,
            cause);
    }

    /// <summary>
    /// Parses the JSON:API <c>errors[]</c> array out of a response body.
    /// </summary>
    /// <param name="body">The raw response body.</param>
    /// <returns>
    /// The parsed errors, or an empty list when the body is absent or is not an AB Connect error
    /// document. Never null.
    /// </returns>
    /// <remarks>
    /// Every member of <see cref="ABConnectApiError"/> is optional, because AB Connect documents the
    /// shape without promising any particular member is present: an observed 404 body carries a null
    /// <c>detail</c>. A <c>status</c> member that arrives as a JSON number rather than a string is
    /// read as its literal text, so a server that deviates from JSON:API on that point still
    /// produces a usable error list rather than an empty one.
    /// </remarks>
    public static IReadOnlyList<ABConnectApiError> ParseErrors(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("errors", out JsonElement errorsElement)
                || errorsElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            List<ABConnectApiError> errors = new(errorsElement.GetArrayLength());
            foreach (JsonElement error in errorsElement.EnumerateArray())
            {
                if (error.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? pointer = null;
                string? parameter = null;
                if (error.TryGetProperty("source", out JsonElement source)
                    && source.ValueKind == JsonValueKind.Object)
                {
                    pointer = ReadText(source, "pointer");
                    parameter = ReadText(source, "parameter");
                }

                errors.Add(new ABConnectApiError(
                    ReadText(error, "title"),
                    ReadText(error, "detail"),
                    ReadText(error, "status"),
                    pointer,
                    parameter));
            }

            return errors;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads one member as text, tolerating a JSON number or boolean where a string was expected and
    /// treating an absent or null member as absent.
    /// </summary>
    private static string? ReadText(JsonElement owner, string propertyName)
    {
        if (!owner.TryGetProperty(propertyName, out JsonElement member))
        {
            return null;
        }

        return member.ValueKind switch
        {
            JsonValueKind.String => member.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => member.GetRawText(),
            _ => null,
        };
    }

    /// <summary>
    /// Reads the <c>Retry-After</c> header of a final 429 in either documented form, delta-seconds or
    /// HTTP-date, and expresses it as a delay rather than an instant. AB Connect does not document
    /// sending the header at all, so an absent or unparseable value yields null.
    /// </summary>
    /// <remarks>
    /// The HTTP-date form needs a reference instant to subtract from. The response's own <c>Date</c>
    /// header is preferred, because it is the sending clock the retry instant was expressed against and
    /// because it makes the resulting delay reproducible; <see cref="TimeProvider.System"/> is the
    /// fallback when the response carries no <c>Date</c>. This is the only clock read in the SDK that
    /// is not driven by an injected <see cref="TimeProvider"/>, because the mapper is static and its
    /// four entry points are a frozen public surface; it is routed through
    /// <see cref="TimeProvider.System"/> rather than <see cref="DateTimeOffset.UtcNow"/> so that the
    /// SDK reads the clock in exactly one way. A retry instant already in the past yields
    /// <see cref="TimeSpan.Zero"/>, never a negative delay.
    /// </remarks>
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } retryAfter)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        if (retryAfter.Date is { } date)
        {
            DateTimeOffset reference = response.Headers.Date ?? TimeProvider.System.GetUtcNow();
            TimeSpan until = date - reference;
            return until > TimeSpan.Zero ? until : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// Composes the message of a status-mapped exception: what came back, for which redacted path,
    /// after how many attempts, followed by the parsed error details or, failing that, an excerpt of
    /// the raw body.
    /// </summary>
    private static string BuildFailureMessage(
        HttpResponseMessage response,
        ABConnectRequestContext context,
        string? body,
        IReadOnlyList<ABConnectApiError> errors,
        int attempts)
    {
        StringBuilder message = new();
        message.Append(CultureInfo.InvariantCulture, $"AB Connect returned HTTP {(int)response.StatusCode}");

        if (!string.IsNullOrWhiteSpace(response.ReasonPhrase))
        {
            message.Append(CultureInfo.InvariantCulture, $" ({response.ReasonPhrase})");
        }

        message.Append(CultureInfo.InvariantCulture, $" for {context.RedactedPath} after {attempts} attempt(s).");

        if (errors.Count > 0)
        {
            // AB Connect quotes the offending query back inside an error detail, so the rendered
            // errors need the same redaction pass as a raw body excerpt.
            message.Append(' ').Append(Redact(Describe(errors)));
        }
        else if (!string.IsNullOrWhiteSpace(body))
        {
            message.Append(CultureInfo.InvariantCulture, $" Response body: {Excerpt(body)}");
        }

        return message.ToString();
    }

    /// <summary>
    /// Composes the message of a format failure on an otherwise successful response, including the
    /// deserializer's own complaint and an excerpt of the body that produced it. Both are redacted:
    /// a <see cref="JsonException"/> quotes the fragment it choked on, which for a body echoing
    /// <c>links.self</c> is the request query and therefore credential-bearing.
    /// </summary>
    private static string BuildFormatMessage(
        HttpResponseMessage response,
        ABConnectRequestContext context,
        byte[] body,
        string reason)
    {
        string decoded = Encoding.UTF8.GetString(body);

        return string.Format(
            CultureInfo.InvariantCulture,
            "AB Connect returned HTTP {0} for {1} but the body could not be read as the expected " +
            "envelope: {2} Response body: {3}",
            (int)response.StatusCode,
            context.RedactedPath,
            Redact(reason),
            string.IsNullOrWhiteSpace(decoded) ? "(empty)" : Excerpt(decoded));
    }

    /// <summary>Renders the parsed errors as one line, in the order AB Connect reported them.</summary>
    private static string Describe(IReadOnlyList<ABConnectApiError> errors)
    {
        IEnumerable<string> parts = errors.Select(static error =>
        {
            string title = error.Title ?? "Error";
            return string.IsNullOrWhiteSpace(error.Detail) ? title : $"{title}: {error.Detail}";
        });

        return string.Join("; ", parts);
    }

    /// <summary>
    /// Truncates a body to <see cref="MaxBodyExcerptLength"/> characters and redacts any credential
    /// the body echoed back, because an exception message must never contain the partner key or an
    /// unredacted signature.
    /// </summary>
    private static string Excerpt(string body)
    {
        string redacted = Redact(body);

        return redacted.Length <= MaxBodyExcerptLength
            ? redacted
            : redacted[..MaxBodyExcerptLength];
    }

    /// <summary>
    /// Replaces the value of every credential-bearing query parameter with a redaction marker. AB
    /// Connect echoes the request query back in <c>links.self</c>, so an excerpt of a response body
    /// is not inherently credential-free the way
    /// <see cref="ABConnectRequestContext.RedactedPath"/> is.
    /// </summary>
    private static string Redact(string text)
        => CredentialPattern.Replace(text, static match => $"{match.Groups[1].Value}=REDACTED");

    /// <summary>
    /// The attempt count to report. The retry handler records one attempt per send; a context that
    /// never reached the handler still describes a single attempt, never zero.
    /// </summary>
    private static int AttemptsOf(ABConnectRequestContext context)
        => context.Attempts > 0 ? context.Attempts : 1;
}
