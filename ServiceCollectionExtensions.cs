using Ardalis.GuardClauses;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OnCourse.ABConnect;
using Polly;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection UseABConnect(this IServiceCollection services, IConfiguration configuration, Func<PolicyBuilder<HttpResponseMessage>, IAsyncPolicy<HttpResponseMessage>>? errorPolicy)
    {
        var settings = new ABConnectSettings();
        configuration.Bind(ABConnectSettings.SectionName, settings);

        services.Configure<ABConnectSettings>(configuration.GetSection(ABConnectSettings.SectionName));

        Guard.Against.NullOrEmpty(settings.ApiUrl, "ABConnect:ApiUrl", "Missing the ABConnect:ApiUrl config in appSettings.json");
        Guard.Against.NullOrEmpty(settings.PartnerId, "ABConnect:PartnerId", "Missing the ABConnect:PartnerId config in appSettings.json");
        Guard.Against.NullOrEmpty(settings.PartnerKey, "ABConnect:PartnerKey", "Missing the ABConnect:PartnerKey config in appSettings.json");

        services.AddHttpClient<IABConnectClient, ABConnectClient>(client =>
        {
            client.BaseAddress = new Uri(settings.ApiUrl);
        })
        .AddTransientHttpErrorPolicy(errorPolicy ?? (p => p.WaitAndRetryAsync(new[]
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10)
        })));

        return services;
    }
}
