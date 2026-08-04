namespace OnCourse.ABConnect.Http;

/// <summary>
/// Per-request state the transport handlers and the response mapper share, carried on
/// <see cref="HttpRequestMessage.Options"/>.
/// </summary>
/// <remarks>
/// This is how <see cref="ABConnectRequestException.RequestPath"/> stays redacted. The path is
/// recorded here before the signing handler appends any credentials, so the mapper reports what was
/// asked for without ever seeing the partner key.
/// </remarks>
public sealed class ABConnectRequestContext
{
    /// <summary>
    /// The key this context is stored under on <see cref="HttpRequestMessage.Options"/>.
    /// </summary>
    public static HttpRequestOptionsKey<ABConnectRequestContext> OptionsKey { get; } =
        new("OnCourse.ABConnect.RequestContext");

    /// <summary>Creates a context for a request.</summary>
    /// <param name="redactedPath">
    /// The request path and query as built by the query builder, before any credentials are
    /// appended. See <see cref="RedactedPath"/>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="redactedPath"/> is null, empty, or whitespace.</exception>
    public ABConnectRequestContext(string redactedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(redactedPath);
        RedactedPath = redactedPath;
    }

    /// <summary>
    /// The request path and query with no credentials in it. The partner key never appears in an
    /// exception, a log line, or a message, and this property is the mechanism that guarantees it.
    /// </summary>
    public string RedactedPath { get; }

    /// <summary>
    /// Whether the request uses a wildcard field set or facet summary, and so must also acquire from
    /// the narrower wildcard token bucket.
    /// </summary>
    public bool IsWildcardRequest { get; init; }

    /// <summary>
    /// How many attempts have been started for this logical request, counting the initial attempt.
    /// The retry handler owns this counter; the response mapper reads it into
    /// <see cref="ABConnectRequestException.Attempts"/>.
    /// </summary>
    public int Attempts { get; set; }

    /// <summary>Attaches this context to a request message.</summary>
    /// <param name="request">The request to attach to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public void AttachTo(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(OptionsKey, this);
    }

    /// <summary>Reads the context attached to a request message, if any.</summary>
    /// <param name="request">The request to read from.</param>
    /// <returns>The attached context, or <see langword="null"/> when the request did not come from this SDK.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public static ABConnectRequestContext? From(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Options.TryGetValue(OptionsKey, out ABConnectRequestContext? context) ? context : null;
    }
}
