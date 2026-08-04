using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OnCourse.ABConnect.Handlers;
using OnCourse.ABConnect.Throttling;

namespace OnCourse.ABConnect;

/// <summary>
/// Registers the AB Connect SDK into a service collection.
/// </summary>
/// <remarks>
/// <para>
/// Every overload performs the same registration: options binding with start-time validation, the
/// system <see cref="TimeProvider"/>, a singleton rate-limiter provider that also serves as
/// <see cref="IABConnectThrottleState"/>, the three transport handlers, the typed client with its
/// base address, and the feed.
/// </para>
/// <para>
/// The rate-limiter provider must be a singleton for the token bucket to mean anything: AB Connect's
/// bucket is per account, so two clients or two hosted commands sharing credentials have to share
/// it. The handler order is fixed at retry, then throttle, then signing. The throttle handler sits
/// inside the retry handler so that every retry attempt spends a token, and the signing handler sits
/// innermost so that a retry occurring after the signature expires gets a fresh signature rather
/// than replaying a stale one.
/// </para>
/// <para>
/// <see cref="HttpClient.Timeout"/> is deliberately left at <see cref="Timeout.InfiniteTimeSpan"/>.
/// <see cref="ABConnectOptions.RequestTimeout"/> is a per-attempt budget, enforced inside
/// <see cref="ABConnectRetryHandler"/>, so a retried call may legitimately take longer in total. A
/// timeout on the <see cref="HttpClient"/> itself would be a per-logical-call budget instead and
/// would kill a call part-way up the retry ladder.
/// </para>
/// </remarks>
public static class ABConnectServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SDK, binding options from the <see cref="ABConnectOptions.SectionName"/>
    /// section of the supplied configuration.
    /// </summary>
    /// <remarks>
    /// This is the overload that makes <c>services.AddABConnect(hostContext.Configuration)</c> work.
    /// It looks up the <c>ABConnect</c> child section itself, so pass the configuration root. To
    /// bind a section under a different name, use the
    /// <see cref="AddABConnect(IServiceCollection, IConfigurationSection)"/> overload; because
    /// <see cref="IConfigurationSection"/> derives from <see cref="IConfiguration"/>, a caller whose
    /// variable is statically typed as a section already selects that overload at compile time.
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configuration">The configuration root or section holding an <c>ABConnect</c> child section.</param>
    /// <returns>A builder for further configuration.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static IABConnectBuilder AddABConnect(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return AddABConnectCore(
            services,
            builder => builder.Bind(configuration.GetSection(ABConnectOptions.SectionName)));
    }

    /// <summary>
    /// Registers the SDK, binding options from an explicitly supplied configuration section.
    /// </summary>
    /// <remarks>
    /// The section is bound as given and is not searched for an <c>ABConnect</c> child, so this is
    /// the overload to use when the settings live somewhere other than the conventional section.
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="section">The section to bind, which need not be named <c>ABConnect</c>.</param>
    /// <returns>A builder for further configuration.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static IABConnectBuilder AddABConnect(this IServiceCollection services, IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);

        return AddABConnectCore(services, builder => builder.Bind(section));
    }

    /// <summary>
    /// Registers the SDK, configuring options in code rather than from configuration.
    /// </summary>
    /// <remarks>
    /// The same validation applies: a registration that leaves
    /// <see cref="ABConnectOptions.PartnerId"/> or <see cref="ABConnectOptions.PartnerKey"/> unset
    /// fails at start-up validation, and would fail again in the <see cref="ABConnectClient"/>
    /// constructor if validation were bypassed.
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configure">The callback that populates the options.</param>
    /// <returns>A builder for further configuration.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static IABConnectBuilder AddABConnect(this IServiceCollection services, Action<ABConnectOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        return AddABConnectCore(services, builder => builder.Configure(configure));
    }

    /// <summary>
    /// Performs the registration described in the type remarks, in the order the specification
    /// requires, and returns the builder handed back to the caller.
    /// </summary>
    /// <remarks>
    /// The options configuration is applied on every call, because "the last configuration wins" is
    /// the behavior every other <c>Configure</c> and <c>Bind</c> in the options system already has.
    /// Everything else is applied exactly once per service collection, tracked by a marker
    /// descriptor: re-running the transport registration would append a second retry, throttle and
    /// signing handler to the same typed client, which would double-spend tokens and sign twice.
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configureOptions">Applies the caller's chosen options source to the options builder.</param>
    /// <returns>A builder over the SDK's typed client registration.</returns>
    private static IABConnectBuilder AddABConnectCore(
        IServiceCollection services,
        Action<OptionsBuilder<ABConnectOptions>> configureOptions)
    {
        // Step 1: options, bound from the caller's source, validated with every problem reported
        // together, and validated again at host start so a misconfigured host fails before it
        // issues its first request.
        var optionsBuilder = services.AddOptions<ABConnectOptions>();
        configureOptions(optionsBuilder);
        optionsBuilder.ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<ABConnectOptions>, ABConnectOptionsValidator>());

        var existingMarker = FindMarker(services);
        if (existingMarker is not null)
        {
            // AddHttpClient(name) adds no configuration action of its own, so this hands back a
            // builder over the client the first call already configured without duplicating it.
            return new ABConnectBuilder(services, services.AddHttpClient(existingMarker.HttpClientName));
        }

        // Step 2: the clock. Every time-dependent behavior in the SDK reads TimeProvider so that
        // signature expiry, retry backoff, token replenishment and DocumentSnapshot.FetchedAtUtc are
        // all testable in virtual time. TryAdd so a test host can substitute a fake clock.
        services.TryAddSingleton(TimeProvider.System);

        // Step 3: the rate limiter. Singleton is load-bearing, not stylistic: AB Connect's bucket is
        // per partner id, so every client and every command sharing credentials must share this
        // instance or the limit is enforced once per registration instead of once per account.
        services.TryAddSingleton<IABConnectRateLimiterProvider, ABConnectRateLimiterProvider>();

        // Step 4: the three handlers. Transient because the HttpClient factory owns handler
        // lifetimes and rotates the chain.
        services.TryAddTransient<ABConnectSigningHandler>();
        services.TryAddTransient<ABConnectThrottleHandler>();
        services.TryAddTransient<ABConnectRetryHandler>();

        // Step 5: the typed client. BaseAddress is set here and only here. Timeout stays infinite;
        // see the type remarks for why ABConnectOptions.RequestTimeout is not applied here.
        var httpClientBuilder = services
            .AddHttpClient<IABConnectClient, ABConnectClient>(static (serviceProvider, httpClient) =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<ABConnectOptions>>().Value;
                httpClient.BaseAddress = options.BaseAddress;
                httpClient.Timeout = Timeout.InfiniteTimeSpan;
            })
            .AddHttpMessageHandler<ABConnectRetryHandler>()
            .AddHttpMessageHandler<ABConnectThrottleHandler>()
            .AddHttpMessageHandler<ABConnectSigningHandler>();

        // Step 6: the paging facade.
        services.TryAddTransient<IABConnectFeed, ABConnectFeed>();

        // Step 7: the throttle state a progress display reads, resolving to the same provider
        // instance the pipeline spends tokens from. A second instance would report a bucket that
        // nothing is drawing on.
        services.TryAddSingleton<IABConnectThrottleState>(
            static serviceProvider => serviceProvider.GetRequiredService<IABConnectRateLimiterProvider>());

        services.Add(ServiceDescriptor.Singleton(new ABConnectRegistrationMarker(httpClientBuilder.Name)));

        return new ABConnectBuilder(services, httpClientBuilder);
    }

    /// <summary>
    /// Finds the marker left by a previous <c>AddABConnect</c> call on the same service collection.
    /// </summary>
    /// <param name="services">The service collection to search.</param>
    /// <returns>The marker, or null when the SDK has not been registered into this collection yet.</returns>
    private static ABConnectRegistrationMarker? FindMarker(IServiceCollection services)
    {
        for (var i = 0; i < services.Count; i++)
        {
            if (services[i].ServiceType == typeof(ABConnectRegistrationMarker)
                && services[i].ImplementationInstance is ABConnectRegistrationMarker marker)
            {
                return marker;
            }
        }

        return null;
    }
}
