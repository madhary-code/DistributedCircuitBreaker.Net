using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DistributedCircuitBreaker.Core;

/// <summary>DI registration helpers.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDistributedCircuitBreaker(this IServiceCollection services, Action<CircuitBreakerOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton<IDistributedCircuitBreaker>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<CircuitBreakerOptions>>().Value;
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DistributedCircuitBreaker>>();
            var store = sp.GetRequiredService<IClusterBreakerStore>();
            return new DistributedCircuitBreaker(store, opts, logger);
        });
        return services;
    }
}
