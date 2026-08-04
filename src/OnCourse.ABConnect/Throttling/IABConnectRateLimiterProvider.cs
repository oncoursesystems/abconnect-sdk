namespace OnCourse.ABConnect.Throttling;

/// <summary>
/// Owns the token buckets that mirror AB Connect's per-account rate limit.
/// </summary>
/// <remarks>
/// Registered as a singleton, because AB Connect's bucket is per account and a per-client bucket
/// would mean nothing: two <see cref="HttpClient"/> instances or two hosted commands sharing
/// credentials must share this. The provider owns a pair of buckets, the main one and a narrower one
/// for wildcard discovery calls, and also serves as the <see cref="IABConnectThrottleState"/> a
/// progress display reads.
/// </remarks>
public interface IABConnectRateLimiterProvider : IABConnectThrottleState, IDisposable
{
    /// <summary>
    /// Acquires one token, waiting asynchronously if the bucket is empty. The calling thread is
    /// never blocked.
    /// </summary>
    /// <param name="isWildcardRequest">
    /// Whether the request uses a wildcard field set or facet summary. A wildcard request acquires
    /// from both the wildcard bucket and the main bucket, so a deliberate discovery run cannot
    /// poison the main bucket.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>
    /// The outcome. Check <see cref="ABConnectRateLimitAcquisition.IsAcquired"/> before proceeding: it
    /// is false when the queue limit was reached, which means the caller should fail rather than send
    /// the request. There is nothing to dispose, because a token bucket does not return a spent token.
    /// </returns>
    ValueTask<ABConnectRateLimitAcquisition> AcquireAsync(
        bool isWildcardRequest,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drains the main bucket and refuses acquisition for the given duration.
    /// </summary>
    /// <remarks>
    /// This is the self-adjusting behavior AB Connect asks clients to implement. It is called by the
    /// retry handler after it computes the backoff for an HTTP 429, never by the throttle handler
    /// alone: at the moment the throttle handler observes a 429 the retry handler sits outside it in
    /// the pipeline and has not yet computed a delay. Without the penalty, a client configured
    /// faster than the account's actual rate produces a steady error rate forever.
    /// </remarks>
    /// <param name="duration">How long acquisition is refused. A non-positive duration is a no-op.</param>
    void ApplyPenalty(TimeSpan duration);

    /// <summary>
    /// Records that an HTTP 429 was observed, incrementing
    /// <see cref="IABConnectThrottleState.ThrottleResponseCount"/>. Recording a 429 has no effect on
    /// the bucket by itself; draining it is <see cref="ApplyPenalty"/>'s job.
    /// </summary>
    void RecordThrottleResponse();
}
