namespace OnCourse.ABConnect;

/// <summary>
/// Configuration for the AB Connect SDK. Bound from the <see cref="SectionName"/> configuration
/// section and validated on start by <see cref="ABConnectOptionsValidator"/>.
/// </summary>
public sealed class ABConnectOptions
{
    /// <summary>
    /// The configuration section these options bind from. The name is stable across major versions
    /// so that existing configuration files continue to bind unchanged.
    /// </summary>
    public const string SectionName = "ABConnect";

    /// <summary>
    /// Root address every request is resolved against. Must be absolute and must end in a trailing
    /// slash, otherwise the last path segment is discarded during relative URI resolution.
    /// </summary>
    public Uri BaseAddress { get; set; } = new("https://api.abconnect.instructure.com/rest/v4.1/");

    /// <summary>
    /// The AB Connect partner identifier, sent as the <c>partner.id</c> query parameter. Required.
    /// </summary>
    public string? PartnerId { get; set; }

    /// <summary>
    /// The AB Connect partner key, used as the HMAC-SHA256 key when minting the request signature.
    /// Required. The key itself is never placed in a URI, a log line, or an exception message.
    /// </summary>
    public string? PartnerKey { get; set; }

    /// <summary>
    /// How far in the future a minted signature expires. A signature is cached and reused until
    /// sixty seconds before its expiry, then re-minted. Must be between one minute and 24 hours.
    /// </summary>
    public TimeSpan SignatureLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Timeout applied per HTTP attempt, not per logical call. A call that is retried may therefore
    /// legitimately take longer in total than this value.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>Page size for list calls. AB caps this at 100.</summary>
    public int PageSize { get; set; } = 100;

    /// <summary>
    /// Permits <c>fields[...]=*</c> and <c>facet_summary=*</c>. Discovery only; not for production
    /// sync. When false, <see cref="Queries.StandardFieldSet.Wildcard"/> is rejected with an
    /// <see cref="ABConnectConfigurationException"/> before a request is made.
    /// </summary>
    public bool AllowWildcardFields { get; set; }

    /// <summary>Client-side token bucket settings that mirror AB Connect's documented limits.</summary>
    public ABConnectThrottleOptions Throttle { get; set; } = new();

    /// <summary>Retry and backoff settings for the transport pipeline.</summary>
    public ABConnectRetryOptions Retry { get; set; } = new();
}

/// <summary>
/// Client-side throttling settings. The defaults reproduce AB Connect's documented token bucket:
/// five tokens added per second, a bucket holding up to 25 tokens, one token consumed per call.
/// </summary>
public sealed class ABConnectThrottleOptions
{
    /// <summary>
    /// Whether client-side throttling is applied. Setting this to false exists for unit tests only;
    /// a production run without a client-side bucket will earn HTTP 429 responses instead.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum tokens the main bucket holds, which is also the maximum burst size. Must be positive.
    /// </summary>
    public int BucketCapacity { get; set; } = 25;

    /// <summary>
    /// Sustained rate at which the main bucket refills, in tokens per second. Must be positive.
    /// </summary>
    public double TokensPerSecond { get; set; } = 5;

    /// <summary>
    /// Maximum tokens the narrower wildcard-discovery bucket holds. Wildcard requests acquire from
    /// both buckets so that a deliberate discovery run cannot drain the main bucket.
    /// </summary>
    public int WildcardBucketCapacity { get; set; } = 2;

    /// <summary>
    /// Sustained refill rate of the wildcard-discovery bucket, in tokens per second. AB Connect
    /// throttles wildcard field and facet requests to two per second.
    /// </summary>
    public double WildcardTokensPerSecond { get; set; } = 2;

    /// <summary>
    /// Maximum number of acquisitions that may wait for a token before further acquisitions fail
    /// rather than queue.
    /// </summary>
    public int QueueLimit { get; set; } = 1000;
}

/// <summary>
/// Retry settings for the transport pipeline. Retries apply to HTTP 429, any 5xx, and transport
/// level failures. HTTP 400, 401, 403, and 404 are terminal for a GET and are never retried.
/// </summary>
public sealed class ABConnectRetryOptions
{
    /// <summary>
    /// Total attempts per logical request, counting the initial attempt. The default of five is one
    /// initial attempt plus four retries. Must be at least 1.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// Base of the exponential backoff. Attempt <c>n</c> waits a uniformly random duration in
    /// <c>[0, min(MaxDelay, BaseDelay * 2^(n-1))]</c>, that is, exponential backoff with full jitter.
    /// </summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound on any single backoff delay.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether a <c>Retry-After</c> response header overrides the computed backoff when present.
    /// AB Connect does not document the header, so it is treated as optional in both directions:
    /// honored when present, in either the delta-seconds or the HTTP-date form, and ignored when absent.
    /// </summary>
    public bool HonorRetryAfter { get; set; } = true;
}
