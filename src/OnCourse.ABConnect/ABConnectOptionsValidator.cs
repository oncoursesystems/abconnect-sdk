using Microsoft.Extensions.Options;

namespace OnCourse.ABConnect;

/// <summary>
/// Validates <see cref="ABConnectOptions"/> at host start. Every rule is checked on every
/// validation pass and all failures are reported together in a single message, so a misconfigured
/// deployment learns about all of its problems at once rather than one restart at a time.
/// </summary>
public sealed class ABConnectOptionsValidator : IValidateOptions<ABConnectOptions>
{
    /// <summary>The shortest signature lifetime the SDK accepts.</summary>
    private static readonly TimeSpan MinimumSignatureLifetime = TimeSpan.FromMinutes(1);

    /// <summary>The longest signature lifetime the SDK accepts.</summary>
    private static readonly TimeSpan MaximumSignatureLifetime = TimeSpan.FromHours(24);

    /// <summary>AB Connect's documented maximum page size for a list call.</summary>
    private const int MaximumPageSize = 100;

    /// <summary>
    /// Validates a bound <see cref="ABConnectOptions"/> instance.
    /// </summary>
    /// <param name="name">The named options instance being validated, or <see langword="null"/> for the default instance.</param>
    /// <param name="options">The options to validate.</param>
    /// <returns>
    /// <see cref="ValidateOptionsResult.Success"/> when every rule passes, otherwise a failure
    /// result whose single message enumerates every rule that failed.
    /// </returns>
    public ValidateOptionsResult Validate(string? name, ABConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.PartnerId))
        {
            failures.Add($"{nameof(ABConnectOptions.PartnerId)} is required and must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.PartnerKey))
        {
            failures.Add($"{nameof(ABConnectOptions.PartnerKey)} is required and must not be empty.");
        }

        if (options.BaseAddress is null)
        {
            failures.Add($"{nameof(ABConnectOptions.BaseAddress)} is required.");
        }
        else
        {
            if (!options.BaseAddress.IsAbsoluteUri)
            {
                failures.Add($"{nameof(ABConnectOptions.BaseAddress)} must be an absolute URI, but was '{options.BaseAddress}'.");
            }
            else if (!options.BaseAddress.AbsoluteUri.EndsWith('/'))
            {
                failures.Add(
                    $"{nameof(ABConnectOptions.BaseAddress)} must end in a trailing slash, but was " +
                    $"'{options.BaseAddress}'. Without it the last path segment is discarded when a " +
                    "relative request URI is resolved.");
            }
        }

        if (options.PageSize is < 1 or > MaximumPageSize)
        {
            failures.Add(
                $"{nameof(ABConnectOptions.PageSize)} must be between 1 and {MaximumPageSize}, but was {options.PageSize}.");
        }

        if (options.SignatureLifetime < MinimumSignatureLifetime || options.SignatureLifetime > MaximumSignatureLifetime)
        {
            failures.Add(
                $"{nameof(ABConnectOptions.SignatureLifetime)} must be between {MinimumSignatureLifetime} and " +
                $"{MaximumSignatureLifetime}, but was {options.SignatureLifetime}.");
        }

        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(ABConnectOptions.RequestTimeout)} must be positive, but was {options.RequestTimeout}.");
        }

        if (options.Retry is null)
        {
            failures.Add($"{nameof(ABConnectOptions.Retry)} is required.");
        }
        else
        {
            if (options.Retry.MaxAttempts < 1)
            {
                failures.Add(
                    $"{nameof(ABConnectOptions.Retry)}.{nameof(ABConnectRetryOptions.MaxAttempts)} must be at least 1, " +
                    $"but was {options.Retry.MaxAttempts}.");
            }

            if (options.Retry.BaseDelay < TimeSpan.Zero)
            {
                failures.Add(
                    $"{nameof(ABConnectOptions.Retry)}.{nameof(ABConnectRetryOptions.BaseDelay)} must not be negative, " +
                    $"but was {options.Retry.BaseDelay}.");
            }

            if (options.Retry.MaxDelay < options.Retry.BaseDelay)
            {
                failures.Add(
                    $"{nameof(ABConnectOptions.Retry)}.{nameof(ABConnectRetryOptions.MaxDelay)} must be at least " +
                    $"{nameof(ABConnectRetryOptions.BaseDelay)}, but was {options.Retry.MaxDelay} against a base delay " +
                    $"of {options.Retry.BaseDelay}.");
            }
        }

        if (options.Throttle is null)
        {
            failures.Add($"{nameof(ABConnectOptions.Throttle)} is required.");
        }
        else
        {
            if (options.Throttle.BucketCapacity < 1)
            {
                failures.Add(
                    $"{nameof(ABConnectOptions.Throttle)}.{nameof(ABConnectThrottleOptions.BucketCapacity)} must be " +
                    $"positive, but was {options.Throttle.BucketCapacity}.");
            }

            if (options.Throttle.TokensPerSecond <= 0)
            {
                failures.Add(
                    $"{nameof(ABConnectOptions.Throttle)}.{nameof(ABConnectThrottleOptions.TokensPerSecond)} must be " +
                    $"positive, but was {options.Throttle.TokensPerSecond}.");
            }

            if (options.Throttle.WildcardBucketCapacity < 1)
            {
                failures.Add(
                    $"{nameof(ABConnectOptions.Throttle)}.{nameof(ABConnectThrottleOptions.WildcardBucketCapacity)} " +
                    $"must be positive, but was {options.Throttle.WildcardBucketCapacity}.");
            }

            if (options.Throttle.WildcardTokensPerSecond <= 0)
            {
                failures.Add(
                    $"{nameof(ABConnectOptions.Throttle)}.{nameof(ABConnectThrottleOptions.WildcardTokensPerSecond)} " +
                    $"must be positive, but was {options.Throttle.WildcardTokensPerSecond}.");
            }

            if (options.Throttle.QueueLimit < 0)
            {
                failures.Add(
                    $"{nameof(ABConnectOptions.Throttle)}.{nameof(ABConnectThrottleOptions.QueueLimit)} must not be " +
                    $"negative, but was {options.Throttle.QueueLimit}.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"AB Connect configuration section '{ABConnectOptions.SectionName}' is invalid: " +
                string.Join(" ", failures));
    }
}
