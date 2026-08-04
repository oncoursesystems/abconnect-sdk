using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OnCourse.ABConnect.Handlers;
using OnCourse.ABConnect.Throttling;
using Xunit;

namespace OnCourse.ABConnect.Tests;

/// <summary>
/// The registration group from specification section 9. Locks down defect 16, the README sample that
/// has never compiled, and the singleton rate limiter that makes the token bucket mean anything.
/// </summary>
/// <remarks>
/// No test here performs network I/O. Resolving the typed client builds an
/// <see cref="HttpClient"/> and its handler chain but sends nothing.
/// </remarks>
public sealed class RegistrationTests
{
    /// <summary>
    /// The devel partner id used by the live gate runs. It is an account name, not a secret, and the
    /// key below is deliberately a fixture value rather than a real one.
    /// </summary>
    private const string PartnerId = "test_account";

    /// <summary>
    /// The partner key from AB Connect's own published authentication worked example.
    /// </summary>
    private const string PartnerKey = "ajk84Hjk93h59skaAJ8732";

    /// <summary>
    /// The name the HTTP client factory files the SDK's typed client under. It is derived rather than
    /// hard-coded so this test says what it depends on: the factory names a typed client registration
    /// after the service type, which for the SDK is <see cref="IABConnectClient"/>.
    /// </summary>
    private static string TypedClientName => typeof(IABConnectClient).Name;

    [Fact]
    public void AddABConnectFromConfigurationResolvesBothPublicInterfaces()
    {
        // This is the exact call AC-14 requires to exist and the README to show:
        // services.AddABConnect(hostContext.Configuration).
        using ServiceProvider provider = BuildProvider(ValidConfiguration());

        var client = provider.GetRequiredService<IABConnectClient>();
        var feed = provider.GetRequiredService<IABConnectFeed>();

        Assert.IsType<ABConnectClient>(client);
        Assert.IsType<ABConnectFeed>(feed);
    }

    [Fact]
    public void AddABConnectBindsEverySettingFromTheConventionalSection()
    {
        using ServiceProvider provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ABConnect:PartnerId"] = PartnerId,
            ["ABConnect:PartnerKey"] = PartnerKey,
            ["ABConnect:BaseAddress"] = "https://api.abconnect.instructure.com/rest/v4.1/",
            ["ABConnect:PageSize"] = "25",
            ["ABConnect:AllowWildcardFields"] = "true",
            ["ABConnect:SignatureLifetime"] = "00:05:00",
            ["ABConnect:Throttle:BucketCapacity"] = "10",
            ["ABConnect:Throttle:TokensPerSecond"] = "2.5",
            ["ABConnect:Retry:MaxAttempts"] = "3",
        });

        ABConnectOptions options = provider.GetRequiredService<IOptions<ABConnectOptions>>().Value;

        Assert.Equal(PartnerId, options.PartnerId);
        Assert.Equal(PartnerKey, options.PartnerKey);
        Assert.Equal(new Uri("https://api.abconnect.instructure.com/rest/v4.1/"), options.BaseAddress);
        Assert.Equal(25, options.PageSize);
        Assert.True(options.AllowWildcardFields);
        Assert.Equal(TimeSpan.FromMinutes(5), options.SignatureLifetime);
        Assert.Equal(10, options.Throttle.BucketCapacity);
        Assert.Equal(2.5, options.Throttle.TokensPerSecond);
        Assert.Equal(3, options.Retry.MaxAttempts);
    }

    [Fact]
    public void TheConfigurationSectionOverloadBindsTheSectionItIsHandedVerbatim()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SomewhereElse:PartnerId"] = PartnerId,
                ["SomewhereElse:PartnerKey"] = PartnerKey,
                ["SomewhereElse:PageSize"] = "42",
            })
            .Build();

        ServiceCollection services = [];
        services.AddABConnect(configuration.GetSection("SomewhereElse"));
        using ServiceProvider provider = services.BuildServiceProvider();

        ABConnectOptions options = provider.GetRequiredService<IOptions<ABConnectOptions>>().Value;
        Assert.Equal(PartnerId, options.PartnerId);
        Assert.Equal(42, options.PageSize);
    }

    [Fact]
    public void TheDelegateOverloadConfiguresOptionsInCode()
    {
        ServiceCollection services = [];
        services.AddABConnect(options =>
        {
            options.PartnerId = PartnerId;
            options.PartnerKey = PartnerKey;
            options.PageSize = 7;
        });
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(7, provider.GetRequiredService<IOptions<ABConnectOptions>>().Value.PageSize);
        Assert.NotNull(provider.GetRequiredService<IABConnectClient>());
    }

    [Fact]
    public void EveryOverloadRejectsANullArgument()
    {
        ServiceCollection services = [];
        IConfigurationRoot configuration = new ConfigurationBuilder().Build();

        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddABConnect(configuration));
        Assert.Throws<ArgumentNullException>(() => services.AddABConnect((IConfiguration)null!));
        Assert.Throws<ArgumentNullException>(() => services.AddABConnect((IConfigurationSection)null!));
        Assert.Throws<ArgumentNullException>(() => services.AddABConnect((Action<ABConnectOptions>)null!));
    }

    [Fact]
    public void MissingConfigurationFailsAtStartupValidationWithEveryProblemInOneMessage()
    {
        // AC-15. The point is not that it throws, it is that a misconfigured host is told everything
        // that is wrong in one go instead of fixing one key per restart.
        using ServiceProvider provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ABConnect:PartnerId"] = "   ",
            ["ABConnect:PageSize"] = "0",
            ["ABConnect:Retry:MaxAttempts"] = "0",
            ["ABConnect:Throttle:TokensPerSecond"] = "0",
        });

        var validator = provider.GetRequiredService<IStartupValidator>();

        OptionsValidationException failure = Assert.Throws<OptionsValidationException>(validator.Validate);
        string message = Assert.Single(failure.Failures);

        Assert.StartsWith(
            $"AB Connect configuration section '{ABConnectOptions.SectionName}' is invalid: ",
            message,
            StringComparison.Ordinal);
        Assert.Contains(nameof(ABConnectOptions.PartnerId), message, StringComparison.Ordinal);
        Assert.Contains(nameof(ABConnectOptions.PartnerKey), message, StringComparison.Ordinal);
        Assert.Contains(nameof(ABConnectOptions.PageSize), message, StringComparison.Ordinal);
        Assert.Contains(nameof(ABConnectRetryOptions.MaxAttempts), message, StringComparison.Ordinal);
        Assert.Contains(nameof(ABConnectThrottleOptions.TokensPerSecond), message, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupValidationPassesForAValidConfiguration()
    {
        using ServiceProvider provider = BuildProvider(ValidConfiguration());

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void NoValidationFailureMessageLeaksThePartnerKey()
    {
        // AC-16. The validator reports that the key is missing, never what it was, and the same must
        // hold when only the surrounding settings are wrong.
        using ServiceProvider provider = BuildProvider(new Dictionary<string, string?>
        {
            ["ABConnect:PartnerId"] = PartnerId,
            ["ABConnect:PartnerKey"] = PartnerKey,
            ["ABConnect:PageSize"] = "9999",
        });

        OptionsValidationException failure = Assert.Throws<OptionsValidationException>(
            provider.GetRequiredService<IStartupValidator>().Validate);

        Assert.DoesNotContain(PartnerKey, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PartnerKey, failure.ToString(), StringComparison.Ordinal);
        Assert.Contains(nameof(ABConnectOptions.PageSize), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoResolutionsOfTheClientShareOneRateLimiter()
    {
        // Defect 16. AB Connect's bucket is per account, so two clients or two hosted commands sharing
        // credentials have to draw on the same bucket or the limit is enforced once per registration.
        using ServiceProvider provider = BuildProvider(ValidConfiguration());

        var first = provider.GetRequiredService<IABConnectClient>();
        var second = provider.GetRequiredService<IABConnectClient>();

        Assert.NotSame(first, second);
        Assert.Same(first.Throttle, second.Throttle);
    }

    [Fact]
    public void TheThrottleStateAConsumerResolvesIsTheProviderThePipelineSpendsFrom()
    {
        using ServiceProvider provider = BuildProvider(ValidConfiguration());

        var rateLimiter = provider.GetRequiredService<IABConnectRateLimiterProvider>();
        var throttleState = provider.GetRequiredService<IABConnectThrottleState>();
        var client = provider.GetRequiredService<IABConnectClient>();

        Assert.Same(rateLimiter, throttleState);
        Assert.Same(rateLimiter, client.Throttle);
        Assert.Same(rateLimiter, provider.GetRequiredService<IABConnectRateLimiterProvider>());
    }

    [Fact]
    public void ACallerWhoSuppliesTheirOwnRateLimiterKeepsIt()
    {
        // The registration uses TryAdd throughout, so a host that has already decided how the bucket
        // works keeps its decision, and the throttle state follows the same instance.
        ServiceCollection services = [];
        StubRateLimiterProvider stub = new();
        services.AddSingleton<IABConnectRateLimiterProvider>(stub);
        services.AddABConnect(ValidConfigurationRoot());

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(stub, provider.GetRequiredService<IABConnectRateLimiterProvider>());
        Assert.Same(stub, provider.GetRequiredService<IABConnectThrottleState>());
        Assert.Same(stub, provider.GetRequiredService<IABConnectClient>().Throttle);
    }

    [Fact]
    public void TheHandlerChainIsRetryThenThrottleThenSigningOutermostFirst()
    {
        // The order is load-bearing, not stylistic. Throttle inside retry means every retry attempt
        // spends a token; signing innermost means a retry that happens after the signature expires
        // gets a fresh one instead of replaying a stale one.
        using ServiceProvider provider = BuildProvider(ValidConfiguration());

        Assert.Equal(
            [typeof(ABConnectRetryHandler), typeof(ABConnectThrottleHandler), typeof(ABConnectSigningHandler)],
            SdkHandlerChain(provider));
    }

    [Fact]
    public void RegisteringTwiceDoesNotAppendASecondCopyOfThePipeline()
    {
        // A duplicated chain would spend two tokens per attempt and append the authentication query
        // fragment twice, which is a broken signature rather than a cosmetic problem.
        ServiceCollection services = [];
        services.AddABConnect(ValidConfigurationRoot());
        services.AddABConnect(ValidConfigurationRoot());

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(
            [typeof(ABConnectRetryHandler), typeof(ABConnectThrottleHandler), typeof(ABConnectSigningHandler)],
            SdkHandlerChain(provider));
        Assert.NotNull(provider.GetRequiredService<IABConnectClient>());
    }

    [Fact]
    public void RegisteringTwiceLetsTheLaterConfigurationWin()
    {
        ServiceCollection services = [];
        services.AddABConnect(options =>
        {
            options.PartnerId = PartnerId;
            options.PartnerKey = PartnerKey;
            options.PageSize = 10;
        });
        services.AddABConnect(options => options.PageSize = 20);

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(20, provider.GetRequiredService<IOptions<ABConnectOptions>>().Value.PageSize);
    }

    [Fact]
    public void TheTypedClientLeavesItsTimeoutInfiniteAndTakesItsBaseAddressFromOptions()
    {
        // Section 4.4: RequestTimeout is a per-attempt budget enforced inside the retry handler. A
        // timeout on the HttpClient would be a per-logical-call budget and would kill a call part-way
        // up the retry ladder.
        using ServiceProvider provider = BuildProvider(ValidConfiguration());

        using HttpClient httpClient = provider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(TypedClientName);

        Assert.Equal(Timeout.InfiniteTimeSpan, httpClient.Timeout);
        Assert.Equal(new Uri("https://api.abconnect.instructure.com/rest/v4.1/"), httpClient.BaseAddress);
    }

    [Fact]
    public void ResolvingTheClientWithoutCredentialsFailsBeforeAnythingIsSent()
    {
        // Validation runs when IOptions<ABConnectOptions>.Value is first read, which the client's own
        // construction does, so a misconfigured registration cannot produce a client at all even in a
        // host that never calls the start-up validator.
        ServiceCollection services = [];
        services.AddABConnect(options => options.PartnerId = PartnerId);
        using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException failure =
            Assert.Throws<OptionsValidationException>(provider.GetRequiredService<IABConnectClient>);

        Assert.Contains(nameof(ABConnectOptions.PartnerKey), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheClientConstructorRejectsMissingCredentialsEvenWhenValidationIsBypassedEntirely()
    {
        // Defect 3, belt and braces. Constructing the client directly skips the options system, and it
        // still refuses rather than signing with an empty key. This is the guard that closes the
        // defect; the registration test above only shows validation usually gets there first.
        using StubRateLimiterProvider rateLimiter = new();
        using HttpClient httpClient = new() { BaseAddress = new Uri("https://example.invalid/") };

        Assert.Throws<ABConnectConfigurationException>(() => new ABConnectClient(
            httpClient,
            Options.Create(new ABConnectOptions { PartnerId = PartnerId, PartnerKey = "   " }),
            rateLimiter,
            NullLogger<ABConnectClient>.Instance));

        Assert.Throws<ABConnectConfigurationException>(() => new ABConnectClient(
            httpClient,
            Options.Create(new ABConnectOptions { PartnerId = null, PartnerKey = PartnerKey }),
            rateLimiter,
            NullLogger<ABConnectClient>.Instance));
    }

    [Fact]
    public void UseABConnectNoLongerExists()
    {
        // AC-14. The version 2 entry point is gone with no obsolete shim, because a shim would let a
        // caller keep the registration that never worked.
        Type[] sdkTypes = typeof(ABConnectOptions).Assembly.GetTypes();

        Assert.DoesNotContain(
            "UseABConnect",
            sdkTypes.SelectMany(type => type.GetMethods().Select(method => method.Name)));
    }

    [Fact]
    public void AddABConnectIsTheOnlyPublicEntryPointAndHasExactlyThreeOverloads()
    {
        var methods = typeof(ABConnectServiceCollectionExtensions)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.DeclaredOnly);

        Assert.Equal(3, methods.Length);
        Assert.All(methods, method => Assert.Equal(nameof(ABConnectServiceCollectionExtensions.AddABConnect), method.Name));
    }

    /// <summary>
    /// Reads the SDK's delegating handlers out of the live chain the HTTP client factory builds, in
    /// outermost-first order, ignoring the factory's own lifetime-tracking and logging handlers.
    /// </summary>
    private static Type[] SdkHandlerChain(IServiceProvider provider)
    {
        HttpMessageHandler? current = provider
            .GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(TypedClientName);

        List<Type> chain = [];
        while (current is DelegatingHandler delegating)
        {
            chain.Add(current.GetType());
            current = delegating.InnerHandler;
        }

        return [.. chain.Where(type => type.Namespace == typeof(ABConnectRetryHandler).Namespace)];
    }

    /// <summary>Builds a provider from an in-memory configuration, registering the SDK from it.</summary>
    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        ServiceCollection services = [];
        services.AddABConnect(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services.BuildServiceProvider();
    }

    /// <summary>A configuration that satisfies every validation rule.</summary>
    private static Dictionary<string, string?> ValidConfiguration() => new()
    {
        ["ABConnect:PartnerId"] = PartnerId,
        ["ABConnect:PartnerKey"] = PartnerKey,
    };

    /// <summary>The same configuration, built, for tests that need the root itself.</summary>
    private static IConfigurationRoot ValidConfigurationRoot()
        => new ConfigurationBuilder().AddInMemoryCollection(ValidConfiguration()).Build();

    /// <summary>
    /// A rate-limiter provider that exists only to prove the registration's TryAdd semantics. Every
    /// member throws, because a test that reaches one has stopped testing registration.
    /// </summary>
    private sealed class StubRateLimiterProvider : IABConnectRateLimiterProvider
    {
        public int AvailableTokens => throw new NotSupportedException();

        public int BucketCapacity => throw new NotSupportedException();

        public long AcquiredCount => throw new NotSupportedException();

        public long WaitCount => throw new NotSupportedException();

        public TimeSpan TotalWaitTime => throw new NotSupportedException();

        public long ThrottleResponseCount => throw new NotSupportedException();

        public ValueTask<ABConnectRateLimitAcquisition> AcquireAsync(
            bool isWildcardRequest,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void ApplyPenalty(TimeSpan duration) => throw new NotSupportedException();

        public void RecordThrottleResponse() => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
