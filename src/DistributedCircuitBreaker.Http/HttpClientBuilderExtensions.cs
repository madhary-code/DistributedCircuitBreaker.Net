using DistributedCircuitBreaker.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DistributedCircuitBreaker.Http;

/// <summary>HttpClientFactory extensions.</summary>
public static class HttpClientBuilderExtensions
{
    public static IHttpClientBuilder AddDualEndpointHttpClient(this IServiceCollection services, string name, Uri primary, Uri secondary)
    {
        return services.AddHttpClient(name)
            .AddHttpMessageHandler(sp => new DualEndpointHandler(sp.GetRequiredService<IDistributedCircuitBreaker>(), primary, secondary, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DualEndpointHandler>>()));
    }
}
