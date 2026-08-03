using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace OnCourse.ABConnect.Throttling;

/// <summary>
/// The default <see cref="IABConnectRateLimiterProvider"/>, backed by a pair of
/// <see cref="TokenBucketRateLimiter"/> instances: one mirroring AB Connect's per-account limit and one
/// narrower bucket for wildcard discovery calls.
/// </summary>
/// <remarks>
/// <para>
/// The main bucket reproduces AB Connect's documented account bucket exactly: a capacity of 25 and a
/// sustained five tokens per second, which is one token every 200 milliseconds, with the oldest waiter
/// served first. Both numbers come from <see cref="ABConnectThrottleOptions"/> and the replenishment
/// interval is derived as the reciprocal of the configured rate, so an account on a customized limit is
/// a configuration change rather than a code change.
/// </para>
/// <para>
/// The second, narrower bucket has a capacity of 2 and a rate of two per second, matching AB Connect's
/// two-per-second limit on wildcard field and facet requests. In normal operation it is never touched,
/// because wildcards are rejected before a request is built unless they are explicitly permitted; it
/// exists so that a deliberate discovery run cannot poison the main bucket.
/// </para>
/// <para>
/// Registered as a singleton: AB Connect's bucket is per account, so a per-client bucket would mean
/// nothing. Two <see cref="HttpClient"/> instances or two hosted commands sharing credentials share
/// this one instance and therefore share the bucket.
/// </para>
/// <para>
/// The bucket accounting itself belongs to <see cref="TokenBucketRateLimiter"/> and is not
/// reimplemented here. What this type adds is the AB Connect policy the framework has no opinion about:
/// charging a wildcard request to two buckets, the post-429 penalty, and the counters a progress
/// display reads.
/// </para>
/// <para>
/// Every counter this type reports describes the <em>main</em> bucket, which is the one that mirrors
/// the account limit and the one a progress display means when it prints <c>(17/25)</c>. Wait time is
/// the exception: a wildcard request waits on both buckets, and both waits are charged to
/// <see cref="TotalWaitTime"/> as a single wait, because from the caller's point of view one request
/// was delayed once.
/// </para>
/// <para>
/// This type does not consult <see cref="ABConnectThrottleOptions.Enabled"/>. Disabling throttling is
/// the transport handler's job: it skips acquisition entirely, so the buckets are simply never asked.
/// </para>
/// </remarks>
public sealed class ABConnectRateLimiterProvider : IABConnectRateLimiterProvider
{
    private readonly TokenBucketRateLimiter _main;
    private readonly TokenBucketRateLimiter _wildcard;
    private readonly TimeProvider _timeProvider;
    private readonly int _capacity;
    private readonly object _penaltyGate = new();

    private long _penaltyUntilTimestamp;
    private long _acquiredCount;
    private long _waitCount;
    private long _totalWaitTicks;
    private long _throttleResponseCount;
    private bool _disposed;

    /// <summary>Creates the provider and its buckets.</summary>
    /// <param name="options">The SDK options supplying the bucket capacities and rates.</param>
    /// <param name="timeProvider">The clock used for wait accounting and penalty expiry.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ABConnectConfigurationException">
    /// <see cref="ABConnectOptions.Throttle"/> is null, or one of its capacities or rates is not
    /// positive. Validated configuration cannot reach this, but a directly constructed options object
    /// can.
    /// </exception>
    public ABConnectRateLimiterProvider(IOptions<ABConnectOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;

        var throttle = options.Value.Throttle
            ?? throw new ABConnectConfigurationException(
                $"AB Connect configuration section '{ABConnectOptions.SectionName}' is invalid: Throttle is required.");

        _capacity = throttle.BucketCapacity;

        try
        {
            _main = new TokenBucketRateLimiter(
                BucketOptions(throttle.BucketCapacity, throttle.TokensPerSecond, throttle.QueueLimit));

            _wildcard = new TokenBucketRateLimiter(
                BucketOptions(
                    throttle.WildcardBucketCapacity,
                    throttle.WildcardTokensPerSecond,
                    throttle.QueueLimit));
        }
        catch (ArgumentException ex)
        {
            throw new ABConnectConfigurationException(
                $"AB Connect configuration section '{ABConnectOptions.SectionName}' is invalid: the throttle capacities must be at least 1, the rates must be greater than zero, and QueueLimit must not be negative.",
                ex);
        }
    }

    /// <inheritdoc />
    public int AvailableTokens =>
        (int)Math.Min(int.MaxValue, Math.Max(0, _main.GetStatistics()?.CurrentAvailablePermits ?? 0));

    /// <inheritdoc />
    public int BucketCapacity => _capacity;

    /// <inheritdoc />
    public long AcquiredCount => Interlocked.Read(ref _acquiredCount);

    /// <inheritdoc />
    public long WaitCount => Interlocked.Read(ref _waitCount);

    /// <inheritdoc />
    public TimeSpan TotalWaitTime => TimeSpan.FromTicks(Interlocked.Read(ref _totalWaitTicks));

    /// <inheritdoc />
    public long ThrottleResponseCount => Interlocked.Read(ref _throttleResponseCount);

    /// <inheritdoc />
    /// <remarks>
    /// A wildcard request takes the narrower bucket's token first and does not get it back if the main
    /// bucket then refuses. That is deliberate: a refusal means the client-side queue is already
    /// saturated, and a discovery run that is being turned away should still be charged for the
    /// discovery rate it asked for rather than being allowed to retry against the narrow bucket for
    /// free.
    /// </remarks>
    public async ValueTask<ABConnectRateLimitAcquisition> AcquireAsync(
        bool isWildcardRequest,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var startedAt = _timeProvider.GetTimestamp();

        await WaitOutPenaltyAsync(cancellationToken).ConfigureAwait(false);

        if (isWildcardRequest)
        {
            // The narrower bucket first: a discovery run must be held to two per second before it is
            // allowed to spend a token from the bucket the production sync depends on.
            using var wildcardLease = await _wildcard.AcquireAsync(1, cancellationToken).ConfigureAwait(false);

            if (!wildcardLease.IsAcquired)
            {
                return Refused(wildcardLease, startedAt);
            }
        }

        using var lease = await _main.AcquireAsync(1, cancellationToken).ConfigureAwait(false);

        var waited = _timeProvider.GetElapsedTime(startedAt);

        if (waited > TimeSpan.Zero)
        {
            Interlocked.Increment(ref _waitCount);
            Interlocked.Add(ref _totalWaitTicks, waited.Ticks);
        }

        if (!lease.IsAcquired)
        {
            return Refused(lease, startedAt);
        }

        Interlocked.Increment(ref _acquiredCount);

        return new ABConnectRateLimitAcquisition(true, waited, null);
    }

    /// <inheritdoc />
    public void ApplyPenalty(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);

        // Spend whatever is left so the burst allowance cannot carry the next attempt straight back
        // into the wall AB Connect just put up. A token bucket does not return permits on release, so
        // acquiring them without using them is exactly a drain.
        var available = AvailableTokens;

        if (available > 0)
        {
            _main.AttemptAcquire(available).Dispose();
        }

        var until = _timeProvider.GetTimestamp()
            + (long)(duration.TotalSeconds * _timeProvider.TimestampFrequency);

        lock (_penaltyGate)
        {
            // A penalty never shortens one already in force: two 429s in flight together must not let
            // the second one's shorter backoff release the first one's.
            if (until > _penaltyUntilTimestamp)
            {
                _penaltyUntilTimestamp = until;
            }
        }
    }

    /// <inheritdoc />
    public void RecordThrottleResponse() => Interlocked.Increment(ref _throttleResponseCount);

    /// <summary>Disposes the underlying token buckets.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _main.Dispose();
        _wildcard.Dispose();
    }

    private static TokenBucketRateLimiterOptions BucketOptions(int capacity, double tokensPerSecond, int queueLimit)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The bucket capacity must be at least 1.");
        }

        if (tokensPerSecond <= 0 || double.IsNaN(tokensPerSecond) || double.IsInfinity(tokensPerSecond))
        {
            throw new ArgumentOutOfRangeException(
                nameof(tokensPerSecond), tokensPerSecond, "The token rate must be a finite number greater than zero.");
        }

        return new TokenBucketRateLimiterOptions
        {
            TokenLimit = capacity,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1d / tokensPerSecond),
            AutoReplenishment = true,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = queueLimit,
        };
    }

    private async ValueTask WaitOutPenaltyAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan remaining;

            lock (_penaltyGate)
            {
                if (_penaltyUntilTimestamp == 0)
                {
                    return;
                }

                var ticks = _penaltyUntilTimestamp - _timeProvider.GetTimestamp();

                if (ticks <= 0)
                {
                    _penaltyUntilTimestamp = 0;
                    return;
                }

                remaining = TimeSpan.FromSeconds((double)ticks / _timeProvider.TimestampFrequency);
            }

            await Task.Delay(remaining, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private ABConnectRateLimitAcquisition Refused(RateLimitLease lease, long startedAt) =>
        new(
            false,
            _timeProvider.GetElapsedTime(startedAt),
            lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : null);
}
