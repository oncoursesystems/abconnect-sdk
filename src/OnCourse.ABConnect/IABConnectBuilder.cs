using Microsoft.Extensions.DependencyInjection;

namespace OnCourse.ABConnect;

/// <summary>
/// The handle returned by <c>AddABConnect</c>, for further configuration of the registration.
/// </summary>
public interface IABConnectBuilder
{
    /// <summary>The service collection the SDK was registered into.</summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// Configures the SDK's typed <see cref="HttpClient"/> registration, for example to swap the
    /// primary handler in a test or to add a caller-supplied handler.
    /// </summary>
    /// <remarks>
    /// The SDK's own three handlers are already in place, ordered retry then throttle then signing.
    /// A handler added here is added outside all three. Do not reorder or remove them: the throttle
    /// handler sits inside the retry handler so every retry spends a token, and the signing handler
    /// sits innermost so a retry after expiry re-signs rather than replaying.
    /// </remarks>
    /// <param name="configure">The configuration to apply to the client builder.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    IABConnectBuilder ConfigureHttpClient(Action<IHttpClientBuilder> configure);
}
