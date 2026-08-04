using Microsoft.Extensions.DependencyInjection;

namespace OnCourse.ABConnect;

/// <summary>
/// The default <see cref="IABConnectBuilder"/>, returned by every <c>AddABConnect</c> overload.
/// </summary>
internal sealed class ABConnectBuilder : IABConnectBuilder
{
    private readonly IHttpClientBuilder _httpClientBuilder;

    /// <summary>Creates the builder.</summary>
    /// <param name="services">The service collection the SDK was registered into.</param>
    /// <param name="httpClientBuilder">The typed client registration the SDK created.</param>
    public ABConnectBuilder(IServiceCollection services, IHttpClientBuilder httpClientBuilder)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(httpClientBuilder);

        Services = services;
        _httpClientBuilder = httpClientBuilder;
    }

    /// <inheritdoc />
    public IServiceCollection Services { get; }

    /// <inheritdoc />
    public IABConnectBuilder ConfigureHttpClient(Action<IHttpClientBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_httpClientBuilder);
        return this;
    }
}
