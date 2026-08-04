namespace OnCourse.ABConnect.Throttling;

/// <summary>
/// The outcome of asking the client-side rate limiter for permission to send one request.
/// </summary>
/// <remarks>
/// <para>
/// This is a plain value rather than a rate-limiter lease because a token bucket does not take its
/// token back when a lease is released: once a request has been permitted, the token is spent. There
/// is nothing for a caller to hold open and nothing for it to dispose, so the caller is handed the
/// answer instead of a resource. Keeping the framework's lease type out of this SDK's own interface
/// also means a consumer reading <see cref="IABConnectRateLimiterProvider"/> does not have to reason
/// about lease lifetimes to understand it.
/// </para>
/// <para>
/// A caller must check <see cref="IsAcquired"/> before sending. False means the wait queue was
/// already full and the request must fail rather than be sent, because sending it anyway is what
/// turns a client-side rate-limit problem into a server-side one.
/// </para>
/// </remarks>
/// <param name="IsAcquired">
/// Whether permission was granted. False when the configured queue limit was reached.
/// </param>
/// <param name="Waited">
/// How long the caller waited for permission, measured across every bucket it had to satisfy.
/// <see cref="TimeSpan.Zero"/> when permission was immediate.
/// </param>
/// <param name="RetryAfter">
/// The limiter's estimate of when a token will next be available, when it offers one. Only
/// meaningful on a refusal.
/// </param>
public readonly record struct ABConnectRateLimitAcquisition(
    bool IsAcquired,
    TimeSpan Waited,
    TimeSpan? RetryAfter)
{
    /// <summary>An immediate grant that did not wait.</summary>
    public static ABConnectRateLimitAcquisition Immediate { get; } = new(true, TimeSpan.Zero, null);
}
