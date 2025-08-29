using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DistributedCircuitBreaker.Core;

/// <summary>
/// Provides extension methods for registering distributed circuit breaker services
/// with the Microsoft.Extensions.DependencyInjection container.
/// </summary>
/// <remarks>
/// <para>
/// These extensions simplify the setup of distributed circuit breakers in ASP.NET Core
/// and other applications using the Microsoft dependency injection container. They handle
/// the registration of required services and configuration validation.
/// </para>
/// <para>
/// <strong>Required Dependencies:</strong>
/// </para>
/// <para>
/// Before calling these extension methods, ensure the following services are registered:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="IClusterBreakerStore"/> - The distributed storage implementation (typically Redis)</description></item>
/// <item><description>Logging services - For operational visibility and debugging</description></item>
/// <item><description>Options services - For configuration binding and validation</description></item>
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
/// // Register store implementation
/// builder.Services.AddSingleton&lt;IClusterBreakerStore, RedisClusterBreakerStore&gt;();
/// 
/// // Register circuit breaker with configuration
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
///     options.Ramp = new RampProfile(
///         new[] { 10, 25, 50, 75, 100 },
///         TimeSpan.FromSeconds(30),
///         0.1);
/// });
/// 
/// var app = builder.Build();
/// </code>
/// <para>Configuration from appsettings.json:</para>
/// <code>
/// // appsettings.json
/// {
///   "CircuitBreaker": {
///     "Key": "orders-service",
///     "Window": "00:02:00",
///     "Bucket": "00:00:10",
///     "MinSamples": 20,
///     "FailureRateToOpen": 0.5,
///     "OpenCooldown": "00:00:30",
///     "HalfOpenMaxProbes": 3,
///     "HalfOpenSuccessesToClose": 5,
///     "Ramp": {
///       "Percentages": [10, 25, 50, 75, 100],
///       "HoldDuration": "00:00:30",
///       "MaxFailureRatePerStep": 0.1
///     }
///   }
/// }
/// 
/// // Program.cs
/// builder.Services.AddDistributedCircuitBreaker(options =&gt;
/// {
///     builder.Configuration.GetSection("CircuitBreaker").Bind(options);
/// });
/// </code>
/// </example>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a distributed circuit breaker with the specified configuration in the service collection.
    /// </summary>
    /// <param name="services">The service collection to add the circuit breaker to.</param>
    /// <param name="configure">
    /// A delegate to configure the circuit breaker options. This delegate is called during
    /// service registration to set up the <see cref="CircuitBreakerOptions"/>.
    /// </param>
    /// <returns>
    /// The same service collection instance for method chaining.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method registers the following services:
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="CircuitBreakerOptions"/> - Configured and validated options</description></item>
    /// <item><description><see cref="IDistributedCircuitBreaker"/> - The core circuit breaker implementation as a singleton</description></item>
    /// </list>
    /// <para>
    /// <strong>Service Lifetime:</strong>
    /// </para>
    /// <para>
    /// The circuit breaker is registered as a singleton to ensure state consistency and
    /// optimal performance. This means the same instance is shared across all requests
    /// and components in the application.
    /// </para>
    /// <para>
    /// <strong>Configuration Validation:</strong>
    /// </para>
    /// <para>
    /// The options are automatically validated at application startup using the
    /// <see cref="CircuitBreakerOptions.Validate"/> method. Invalid configurations
    /// will prevent the application from starting with a descriptive error message.
    /// </para>
    /// <para>
    /// <strong>Required Dependencies:</strong>
    /// </para>
    /// <para>
    /// This method assumes the following services are already registered:
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="IClusterBreakerStore"/> - Must be registered before calling this method</description></item>
    /// <item><description><c>ILogger&lt;DistributedCircuitBreaker&gt;</c> - Logging services must be configured</description></item>
    /// </list>
    /// <para>
    /// <strong>Thread Safety:</strong>
    /// </para>
    /// <para>
    /// The registered circuit breaker implementation is thread-safe and designed for
    /// concurrent access from multiple threads and requests.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="configure"/> is null.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown during application startup if required dependencies are not registered
    /// or if the configuration is invalid.
    /// </exception>
    /// <example>
    /// <para>Basic configuration with hardcoded values:</para>
    /// <code>
    /// services.AddDistributedCircuitBreaker(options =&gt;
    /// {
    ///     options.Key = "payment-service";
    ///     options.Window = TimeSpan.FromSeconds(120);
    ///     options.Bucket = TimeSpan.FromSeconds(10);
    ///     options.MinSamples = 50;
    ///     options.FailureRateToOpen = 0.6;
    ///     options.OpenCooldown = TimeSpan.FromSeconds(45);
    ///     options.HalfOpenMaxProbes = 2;
    ///     options.HalfOpenSuccessesToClose = 3;
    ///     options.Ramp = new RampProfile(
    ///         new[] { 20, 50, 100 },
    ///         TimeSpan.FromSeconds(60),
    ///         0.15);
    /// });
    /// </code>
    /// <para>Configuration from IConfiguration:</para>
    /// <code>
    /// services.AddDistributedCircuitBreaker(options =&gt;
    /// {
    ///     configuration.GetSection("PaymentServiceCircuitBreaker").Bind(options);
    /// });
    /// </code>
    /// <para>Environment-specific configuration:</para>
    /// <code>
    /// services.AddDistributedCircuitBreaker(options =&gt;
    /// {
    ///     var env = serviceProvider.GetRequiredService&lt;IWebHostEnvironment&gt;();
    ///     if (env.IsDevelopment())
    ///     {
    ///         // More lenient settings for development
    ///         options.FailureRateToOpen = 0.8;
    ///         options.MinSamples = 5;
    ///     }
    ///     else
    ///     {
    ///         // Production settings
    ///         configuration.GetSection("CircuitBreaker").Bind(options);
    ///     }
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddDistributedCircuitBreaker(this IServiceCollection services, Action<CircuitBreakerOptions> configure)
    {
        // Configure options with validation
        services.Configure(configure);
        
        // Register the circuit breaker as a singleton
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
