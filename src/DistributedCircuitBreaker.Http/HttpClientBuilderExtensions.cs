using DistributedCircuitBreaker.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DistributedCircuitBreaker.Http;

/// <summary>
/// Provides extension methods for integrating distributed circuit breaker functionality
/// with the .NET HTTP client factory and <see cref="IHttpClientBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// These extensions simplify the configuration of HTTP clients with automatic primary/secondary
/// endpoint failover capabilities. They integrate seamlessly with the ASP.NET Core dependency
/// injection container and HTTP client factory patterns.
/// </para>
/// <para>
/// <strong>Prerequisites:</strong>
/// </para>
/// <para>
/// Before using these extensions, ensure the following services are registered:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="IDistributedCircuitBreaker"/> - The core circuit breaker implementation</description></item>
/// <item><description><see cref="IClusterBreakerStore"/> - Distributed storage backend (typically Redis)</description></item>
/// <item><description>Logging services - For operational visibility and debugging</description></item>
/// </list>
/// <para>
/// <strong>Integration Benefits:</strong>
/// </para>
/// <list type="bullet">
/// <item><description>Automatic endpoint failover without application code changes</description></item>
/// <item><description>Centralized circuit breaker configuration and monitoring</description></item>
/// <item><description>Compatible with existing HTTP client patterns and middleware</description></item>
/// <item><description>Supports HTTP client factory features like policies, logging, and named clients</description></item>
/// </list>
/// </remarks>
/// <example>
/// <para>Complete setup in ASP.NET Core:</para>
/// <code>
/// var builder = WebApplication.CreateBuilder(args);
/// 
/// // Register Redis connection
/// builder.Services.AddSingleton&lt;IConnectionMultiplexer&gt;(
///     ConnectionMultiplexer.Connect("localhost:6379"));
/// 
/// // Register circuit breaker dependencies
/// builder.Services.AddSingleton&lt;IClusterBreakerStore, RedisClusterBreakerStore&gt;();
/// builder.Services.AddDistributedCircuitBreaker(options =&gt;
/// {
///     options.Key = "orders-service";
///     options.Window = TimeSpan.FromMinutes(2);
///     options.Bucket = TimeSpan.FromSeconds(10);
///     options.MinSamples = 20;
///     options.FailureRateToOpen = 0.5;
///     options.OpenCooldown = TimeSpan.FromSeconds(30);
///     options.HalfOpenMaxProbes = 3;
///     options.HalfOpenSuccessesToClose = 5;
/// });
/// 
/// // Configure HTTP client with dual endpoints
/// builder.Services.AddDualEndpointHttpClient("OrdersApi",
///     primary: new Uri("https://orders-api.company.com"),
///     secondary: new Uri("https://orders-backup.company.com"));
/// 
/// var app = builder.Build();
/// </code>
/// <para>Using the configured HTTP client:</para>
/// <code>
/// public class OrdersService
/// {
///     private readonly HttpClient _httpClient;
///     
///     public OrdersService(IHttpClientFactory httpClientFactory)
///     {
///         _httpClient = httpClientFactory.CreateClient("OrdersApi");
///     }
///     
///     public async Task&lt;Order&gt; GetOrderAsync(int orderId)
///     {
///         // Automatically uses primary or secondary endpoint based on circuit breaker state
///         var response = await _httpClient.GetAsync($"/api/orders/{orderId}");
///         response.EnsureSuccessStatusCode();
///         return await response.Content.ReadFromJsonAsync&lt;Order&gt;();
///     }
/// }
/// </code>
/// </example>
public static class HttpClientBuilderExtensions
{
    /// <summary>
    /// Configures an HTTP client with dual endpoint support using a distributed circuit breaker
    /// for automatic primary/secondary failover.
    /// </summary>
    /// <param name="services">The service collection to configure the HTTP client in.</param>
    /// <param name="name">
    /// The logical name of the HTTP client to configure. This name is used with
    /// <see cref="IHttpClientFactory"/> to retrieve the configured client instance.
    /// </param>
    /// <param name="primary">
    /// The primary endpoint URI to use under normal conditions. Should be the preferred
    /// service endpoint with better performance, features, or geographic proximity.
    /// </param>
    /// <param name="secondary">
    /// The secondary endpoint URI to use during primary endpoint failures. Should be
    /// a reliable fallback that can handle the same API requests as the primary.
    /// </param>
    /// <returns>
    /// An <see cref="IHttpClientBuilder"/> that can be used to further configure
    /// the HTTP client with additional handlers, policies, or settings.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/>, <paramref name="name"/>, 
    /// <paramref name="primary"/>, or <paramref name="secondary"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="name"/> is empty or when the URIs are not absolute.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This method configures a named HTTP client with the <see cref="DualEndpointHandler"/>
    /// that automatically routes requests based on circuit breaker state. The handler is
    /// added to the HTTP client's message handler pipeline.
    /// </para>
    /// <para>
    /// <strong>Handler Pipeline Order:</strong>
    /// </para>
    /// <para>
    /// The <see cref="DualEndpointHandler"/> is added as the outermost handler in the pipeline,
    /// ensuring that endpoint selection occurs before any other processing. This allows
    /// other handlers (authentication, logging, retry policies) to work normally with
    /// the selected endpoint.
    /// </para>
    /// <para>
    /// <strong>Endpoint Requirements:</strong>
    /// </para>
    /// <list type="bullet">
    /// <item><description>Both URIs must be absolute (include scheme and host)</description></item>
    /// <item><description>Endpoints should serve compatible APIs for seamless failover</description></item>
    /// <item><description>Path components are ignored (request paths are preserved)</description></item>
    /// <item><description>Authentication and other endpoint-specific configurations should be compatible</description></item>
    /// </list>
    /// <para>
    /// <strong>Circuit Breaker Dependency:</strong>
    /// </para>
    /// <para>
    /// This method assumes that <see cref="IDistributedCircuitBreaker"/> is already
    /// registered in the service collection. If not registered, the application will
    /// fail at runtime when trying to create the HTTP client.
    /// </para>
    /// </remarks>
    /// <example>
    /// <para>Basic dual endpoint configuration:</para>
    /// <code>
    /// services.AddDualEndpointHttpClient("PaymentsApi",
    ///     primary: new Uri("https://payments.company.com"),
    ///     secondary: new Uri("https://payments-backup.company.com"));
    /// </code>
    /// <para>Configuration with additional HTTP client settings:</para>
    /// <code>
    /// services.AddDualEndpointHttpClient("PaymentsApi",
    ///         primary: new Uri("https://payments.company.com"),
    ///         secondary: new Uri("https://payments-backup.company.com"))
    ///     .ConfigureHttpClient(client =&gt;
    ///     {
    ///         client.Timeout = TimeSpan.FromSeconds(30);
    ///         client.DefaultRequestHeaders.Add("User-Agent", "MyApp/1.0");
    ///     })
    ///     .AddHttpMessageHandler&lt;AuthenticationHandler&gt;()
    ///     .AddPolicyHandler(GetRetryPolicy());
    /// </code>
    /// <para>Multiple clients with different endpoints:</para>
    /// <code>
    /// // Orders service
    /// services.AddDualEndpointHttpClient("OrdersApi",
    ///     primary: new Uri("https://orders.company.com"),
    ///     secondary: new Uri("https://orders-backup.company.com"));
    /// 
    /// // Inventory service  
    /// services.AddDualEndpointHttpClient("InventoryApi", 
    ///     primary: new Uri("https://inventory.company.com"),
    ///     secondary: new Uri("https://inventory-backup.company.com"));
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddDualEndpointHttpClient(this IServiceCollection services, string name, Uri primary, Uri secondary)
    {
        if (services == null) throw new ArgumentNullException(nameof(services));
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("HTTP client name cannot be null or empty", nameof(name));
        if (primary == null) throw new ArgumentNullException(nameof(primary));
        if (secondary == null) throw new ArgumentNullException(nameof(secondary));
        if (!primary.IsAbsoluteUri) throw new ArgumentException("Primary URI must be absolute", nameof(primary));
        if (!secondary.IsAbsoluteUri) throw new ArgumentException("Secondary URI must be absolute", nameof(secondary));

        return services.AddHttpClient(name)
            .AddHttpMessageHandler(sp => new DualEndpointHandler(
                sp.GetRequiredService<IDistributedCircuitBreaker>(), 
                primary, 
                secondary, 
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DualEndpointHandler>>()));
    }
}
