using Microsoft.Extensions.Options;
using Xunit;

namespace OnCourse.ABConnect.Tests;

/// <summary>
/// Covers <see cref="ABConnectOptionsValidator"/> rule by rule. Every rule exists because a
/// misconfigured deployment that starts anyway is the failure mode this release is meant to remove:
/// a base address without a trailing slash silently drops a path segment, a page size of zero
/// returns a meta block and no rows, and a throttle rate of zero means no request ever comes due.
/// The validator reports every failure at once, so the tests assert on which rules fired rather
/// than only that validation failed.
/// </summary>
public sealed class OptionsValidationTests
{
    private static readonly ABConnectOptionsValidator Validator = new();

    /// <summary>A fully valid options instance, the baseline every negative case perturbs.</summary>
    private static ABConnectOptions Valid() => new()
    {
        PartnerId = "test_account",
        PartnerKey = "ajk84Hjk93h59skaAJ8732",
    };

    [Fact]
    public void TheShippedDefaultsPlusCredentialsAreValid()
    {
        ValidateOptionsResult result = Validator.Validate(Options.DefaultName, Valid());

        Assert.True(result.Succeeded);
        Assert.Null(result.FailureMessage);
    }

    [Fact]
    public void ANullOptionsInstanceIsRejectedRatherThanTreatedAsDefaults()
        => Assert.Throws<ArgumentNullException>(() => Validator.Validate(null, null!));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingPartnerIdFails(string? partnerId)
    {
        ABConnectOptions options = Valid();
        options.PartnerId = partnerId;

        AssertFailsMentioning(options, nameof(ABConnectOptions.PartnerId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingPartnerKeyFails(string? partnerKey)
    {
        ABConnectOptions options = Valid();
        options.PartnerKey = partnerKey;

        AssertFailsMentioning(options, nameof(ABConnectOptions.PartnerKey));
    }

    [Fact]
    public void ANullBaseAddressFails()
    {
        ABConnectOptions options = Valid();
        options.BaseAddress = null!;

        AssertFailsMentioning(options, nameof(ABConnectOptions.BaseAddress));
    }

    [Fact]
    public void ARelativeBaseAddressFails()
    {
        ABConnectOptions options = Valid();
        options.BaseAddress = new Uri("rest/v4.1/", UriKind.Relative);

        string message = AssertFailsMentioning(options, nameof(ABConnectOptions.BaseAddress));
        Assert.Contains("absolute", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without the trailing slash, resolving a relative request URI against the base address
    /// discards the last path segment, so <c>.../rest/v4.1</c> plus <c>standards</c> becomes
    /// <c>.../rest/standards</c>. The validator refuses to let that reach a request.
    /// </summary>
    [Fact]
    public void ABaseAddressWithoutATrailingSlashFails()
    {
        ABConnectOptions options = Valid();
        options.BaseAddress = new Uri("https://api.abconnect.instructure.com/rest/v4.1");

        string message = AssertFailsMentioning(options, nameof(ABConnectOptions.BaseAddress));
        Assert.Contains("trailing slash", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public void APageSizeOutsideOneToOneHundredFails(int pageSize)
    {
        ABConnectOptions options = Valid();
        options.PageSize = pageSize;

        AssertFailsMentioning(options, nameof(ABConnectOptions.PageSize));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(25)]
    [InlineData(100)]
    public void APageSizeInsideOneToOneHundredPasses(int pageSize)
    {
        ABConnectOptions options = Valid();
        options.PageSize = pageSize;

        Assert.True(Validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(59)]
    [InlineData(-1)]
    public void ASignatureLifetimeShorterThanAMinuteFails(int seconds)
    {
        ABConnectOptions options = Valid();
        options.SignatureLifetime = TimeSpan.FromSeconds(seconds);

        AssertFailsMentioning(options, nameof(ABConnectOptions.SignatureLifetime));
    }

    [Fact]
    public void ASignatureLifetimeLongerThanADayFails()
    {
        ABConnectOptions options = Valid();
        options.SignatureLifetime = TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1);

        AssertFailsMentioning(options, nameof(ABConnectOptions.SignatureLifetime));
    }

    [Theory]
    [InlineData(60)]
    [InlineData(900)]
    [InlineData(86400)]
    public void ASignatureLifetimeOnEitherBoundaryPasses(int seconds)
    {
        ABConnectOptions options = Valid();
        options.SignatureLifetime = TimeSpan.FromSeconds(seconds);

        Assert.True(Validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveRequestTimeoutFails(int seconds)
    {
        ABConnectOptions options = Valid();
        options.RequestTimeout = TimeSpan.FromSeconds(seconds);

        AssertFailsMentioning(options, nameof(ABConnectOptions.RequestTimeout));
    }

    [Fact]
    public void ANullRetrySectionFails()
    {
        ABConnectOptions options = Valid();
        options.Retry = null!;

        AssertFailsMentioning(options, nameof(ABConnectOptions.Retry));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void FewerThanOneRetryAttemptFails(int attempts)
    {
        ABConnectOptions options = Valid();
        options.Retry.MaxAttempts = attempts;

        AssertFailsMentioning(options, nameof(ABConnectRetryOptions.MaxAttempts));
    }

    [Fact]
    public void ANegativeBaseDelayFails()
    {
        ABConnectOptions options = Valid();
        options.Retry.BaseDelay = TimeSpan.FromSeconds(-1);

        AssertFailsMentioning(options, nameof(ABConnectRetryOptions.BaseDelay));
    }

    /// <summary>
    /// A maximum delay below the base delay would make the cap a floor, which is the mutation the
    /// retry tests treat as a defect. It is refused at configuration time as well.
    /// </summary>
    [Fact]
    public void AMaxDelayBelowTheBaseDelayFails()
    {
        ABConnectOptions options = Valid();
        options.Retry.BaseDelay = TimeSpan.FromSeconds(10);
        options.Retry.MaxDelay = TimeSpan.FromSeconds(5);

        AssertFailsMentioning(options, nameof(ABConnectRetryOptions.MaxDelay));
    }

    [Fact]
    public void AZeroBaseDelayWithAZeroMaxDelayPasses()
    {
        ABConnectOptions options = Valid();
        options.Retry.BaseDelay = TimeSpan.Zero;
        options.Retry.MaxDelay = TimeSpan.Zero;

        Assert.True(Validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void ANullThrottleSectionFails()
    {
        ABConnectOptions options = Valid();
        options.Throttle = null!;

        AssertFailsMentioning(options, nameof(ABConnectOptions.Throttle));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-25)]
    public void ANonPositiveBucketCapacityFails(int capacity)
    {
        ABConnectOptions options = Valid();
        options.Throttle.BucketCapacity = capacity;

        AssertFailsMentioning(options, nameof(ABConnectThrottleOptions.BucketCapacity));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-5d)]
    public void ANonPositiveTokenRateFails(double tokensPerSecond)
    {
        ABConnectOptions options = Valid();
        options.Throttle.TokensPerSecond = tokensPerSecond;

        AssertFailsMentioning(options, nameof(ABConnectThrottleOptions.TokensPerSecond));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void ANonPositiveWildcardBucketCapacityFails(int capacity)
    {
        ABConnectOptions options = Valid();
        options.Throttle.WildcardBucketCapacity = capacity;

        AssertFailsMentioning(options, nameof(ABConnectThrottleOptions.WildcardBucketCapacity));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    public void ANonPositiveWildcardTokenRateFails(double tokensPerSecond)
    {
        ABConnectOptions options = Valid();
        options.Throttle.WildcardTokensPerSecond = tokensPerSecond;

        AssertFailsMentioning(options, nameof(ABConnectThrottleOptions.WildcardTokensPerSecond));
    }

    [Fact]
    public void ANegativeQueueLimitFails()
    {
        ABConnectOptions options = Valid();
        options.Throttle.QueueLimit = -1;

        AssertFailsMentioning(options, nameof(ABConnectThrottleOptions.QueueLimit));
    }

    /// <summary>
    /// A queue limit of zero is legal and means "never queue": an acquisition that would have to
    /// wait fails immediately with a retry hint instead.
    /// </summary>
    [Fact]
    public void AZeroQueueLimitPasses()
    {
        ABConnectOptions options = Valid();
        options.Throttle.QueueLimit = 0;

        Assert.True(Validator.Validate(null, options).Succeeded);
    }

    /// <summary>
    /// The point of collecting failures rather than returning the first one: a deployment with
    /// several problems learns about all of them in one restart.
    /// </summary>
    [Fact]
    public void EveryFailedRuleIsReportedInOneMessage()
    {
        ABConnectOptions options = new()
        {
            PartnerId = null,
            PartnerKey = " ",
            BaseAddress = new Uri("https://example.invalid/rest/v4.1"),
            PageSize = 0,
            SignatureLifetime = TimeSpan.Zero,
            RequestTimeout = TimeSpan.Zero,
        };
        options.Retry.MaxAttempts = 0;
        options.Retry.BaseDelay = TimeSpan.FromSeconds(-1);
        options.Throttle.BucketCapacity = 0;
        options.Throttle.TokensPerSecond = 0;
        options.Throttle.WildcardBucketCapacity = 0;
        options.Throttle.WildcardTokensPerSecond = 0;
        options.Throttle.QueueLimit = -1;

        ValidateOptionsResult result = Validator.Validate(null, options);

        Assert.False(result.Succeeded);
        string message = Assert.IsType<string>(result.FailureMessage);

        Assert.Contains($"section '{ABConnectOptions.SectionName}' is invalid", message, StringComparison.Ordinal);

        foreach (string rule in new[]
        {
            nameof(ABConnectOptions.PartnerId),
            nameof(ABConnectOptions.PartnerKey),
            nameof(ABConnectOptions.BaseAddress),
            nameof(ABConnectOptions.PageSize),
            nameof(ABConnectOptions.SignatureLifetime),
            nameof(ABConnectOptions.RequestTimeout),
            nameof(ABConnectRetryOptions.MaxAttempts),
            nameof(ABConnectRetryOptions.BaseDelay),
            nameof(ABConnectThrottleOptions.BucketCapacity),
            nameof(ABConnectThrottleOptions.TokensPerSecond),
            nameof(ABConnectThrottleOptions.WildcardBucketCapacity),
            nameof(ABConnectThrottleOptions.WildcardTokensPerSecond),
            nameof(ABConnectThrottleOptions.QueueLimit),
        })
        {
            Assert.Contains(rule, message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A failure message is composed from configuration values, so it must never quote the partner
    /// key back at whoever reads the log.
    /// </summary>
    [Fact]
    public void AFailureMessageNeverQuotesThePartnerKey()
    {
        ABConnectOptions options = Valid();
        options.PageSize = 0;

        string message = AssertFailsMentioning(options, nameof(ABConnectOptions.PageSize));

        Assert.DoesNotContain("ajk84Hjk93h59skaAJ8732", message, StringComparison.Ordinal);
    }

    private static string AssertFailsMentioning(ABConnectOptions options, string rule)
    {
        ValidateOptionsResult result = Validator.Validate(null, options);

        Assert.False(result.Succeeded);
        string message = Assert.IsType<string>(result.FailureMessage);
        Assert.Contains(rule, message, StringComparison.Ordinal);

        return message;
    }
}
