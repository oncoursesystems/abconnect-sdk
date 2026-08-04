using Microsoft.Extensions.DependencyInjection;

namespace OnCourse.ABConnect;

/// <summary>
/// A marker left in the service collection by the first <c>AddABConnect</c> call, so that a second
/// call does not append a second copy of the transport pipeline.
/// </summary>
/// <remarks>
/// A duplicated pipeline is not a cosmetic problem: two throttle handlers would spend two tokens per
/// attempt and two signing handlers would append the authentication query fragment twice. The marker
/// carries the typed client's factory name so the repeat call can hand back a builder over the
/// registration that already exists rather than creating a second one.
/// </remarks>
/// <param name="HttpClientName">The <see cref="IHttpClientBuilder.Name"/> of the SDK's typed client registration.</param>
internal sealed record ABConnectRegistrationMarker(string HttpClientName);
