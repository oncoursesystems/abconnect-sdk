namespace OnCourse.ABConnect.Throttling;

/// <summary>
/// Read-only counters describing the client-side token bucket, so a progress display can show what
/// the SDK is doing rather than guessing.
/// </summary>
/// <remarks>
/// Every member is a live snapshot. Reading two members is not atomic with respect to each other,
/// which is acceptable for display but is not a basis for control flow: to slow down, configure
/// <see cref="ABConnectThrottleOptions"/>, do not poll these.
/// </remarks>
public interface IABConnectThrottleState
{
    /// <summary>
    /// Tokens currently available in the main bucket, between zero and
    /// <see cref="BucketCapacity"/>.
    /// </summary>
    int AvailableTokens { get; }

    /// <summary>
    /// The main bucket's capacity, so a caller can render a progress string such as
    /// <c>(17/25)</c> without hard-coding the denominator.
    /// </summary>
    int BucketCapacity { get; }

    /// <summary>Total tokens acquired since the provider was created, across all requests including retries.</summary>
    long AcquiredCount { get; }

    /// <summary>How many acquisitions had to wait for a token rather than taking one immediately.</summary>
    long WaitCount { get; }

    /// <summary>Total time spent waiting for tokens, summed across all acquisitions that waited.</summary>
    TimeSpan TotalWaitTime { get; }

    /// <summary>
    /// How many HTTP 429 responses have been observed. A non-zero value means the client-side bucket
    /// is configured faster than the account's actual rate, since a correctly configured bucket
    /// should keep requests inside the documented limit.
    /// </summary>
    long ThrottleResponseCount { get; }
}
