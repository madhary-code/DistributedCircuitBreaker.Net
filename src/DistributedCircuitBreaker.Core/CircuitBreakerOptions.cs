using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DistributedCircuitBreaker.Core;

/// <summary>
/// Configuration options for the <see cref="DistributedCircuitBreaker"/> that controls failure detection,
/// state transitions, and recovery behavior in a distributed environment.
/// </summary>
/// <remarks>
/// <para>
/// The circuit breaker operates using a sliding window approach to track failures over time.
/// When the failure rate exceeds the configured threshold within the observation window,
/// the circuit breaker transitions to the Open state, redirecting traffic to secondary endpoints.
/// </para>
/// <para>
/// This class implements <see cref="IValidateOptions{TOptions}"/> to ensure configuration integrity
/// at application startup. Invalid configurations will prevent the application from starting.
/// </para>
/// <example>
/// <para>Basic configuration for an orders service:</para>
/// <code>
/// var options = new CircuitBreakerOptions(
///     key: "orders-service",
///     window: TimeSpan.FromMinutes(2),        // 2-minute observation window
///     bucket: TimeSpan.FromSeconds(10),       // 10-second buckets for granular tracking
///     minSamples: 20,                         // Need at least 20 requests before opening
///     failureRateToOpen: 0.5,                 // Open when 50% of requests fail
///     openCooldown: TimeSpan.FromSeconds(30), // Stay open for 30 seconds
///     halfOpenMaxProbes: 3,                   // Allow 3 probe requests in half-open
///     halfOpenSuccessesToClose: 5,            // Need 5 consecutive successes to close
///     ramp: new RampProfile(
///         new[] { 10, 25, 50, 75, 100 },      // Gradual traffic ramp: 10% → 100%
///         TimeSpan.FromSeconds(30),           // Hold each level for 30 seconds
///         0.1                                 // Max 10% failure rate during ramp
///     )
/// );
/// </code>
/// </example>
/// </remarks>
public sealed class CircuitBreakerOptions : IValidateOptions<CircuitBreakerOptions>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CircuitBreakerOptions"/> class with the specified configuration.
    /// </summary>
    /// <param name="key">Unique identifier for this circuit breaker instance. Used as Redis key prefix for distributed coordination.</param>
    /// <param name="window">Total time window for failure rate calculation. Must be greater than <paramref name="bucket"/>.</param>
    /// <param name="bucket">Time duration of each bucket within the sliding window. Smaller buckets provide more granular tracking.</param>
    /// <param name="minSamples">Minimum number of requests required in the window before the circuit breaker can open.</param>
    /// <param name="failureRateToOpen">Failure rate threshold (0.0 to 1.0) that triggers the circuit breaker to open.</param>
    /// <param name="openCooldown">Duration the circuit breaker remains open before transitioning to half-open state.</param>
    /// <param name="halfOpenMaxProbes">Maximum number of probe requests allowed simultaneously in half-open state.</param>
    /// <param name="halfOpenSuccessesToClose">Number of consecutive successful probe requests required to close the circuit breaker.</param>
    /// <param name="ramp">Recovery ramp profile that defines gradual traffic restoration after the circuit breaker closes.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> or <paramref name="ramp"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty or whitespace.</exception>
    public CircuitBreakerOptions(string key, TimeSpan window, TimeSpan bucket, int minSamples, double failureRateToOpen, TimeSpan openCooldown, int halfOpenMaxProbes, int halfOpenSuccessesToClose, RampProfile ramp)
    {
        Key = key;
        Window = window;
        Bucket = bucket;
        MinSamples = minSamples;
        FailureRateToOpen = failureRateToOpen;
        OpenCooldown = openCooldown;
        HalfOpenMaxProbes = halfOpenMaxProbes;
        HalfOpenSuccessesToClose = halfOpenSuccessesToClose;
        Ramp = ramp;
    }

    /// <summary>
    /// Gets the unique identifier for this circuit breaker instance.
    /// </summary>
    /// <value>
    /// A non-null, non-empty string that serves as the Redis key prefix for distributed state coordination.
    /// Multiple application instances using the same key will share circuit breaker state.
    /// </value>
    /// <remarks>
    /// <para>
    /// Choose meaningful keys that represent the protected resource or service endpoint.
    /// Examples: "orders-api", "payment-service", "user-auth".
    /// </para>
    /// <para>
    /// Keys should be unique across different circuit breakers in your application to prevent
    /// state interference between unrelated protected resources.
    /// </para>
    /// </remarks>
    [Required]
    public string Key { get; }

    /// <summary>
    /// Gets the total time window used for failure rate calculation.
    /// </summary>
    /// <value>
    /// A <see cref="TimeSpan"/> between 1 second and 1 hour that defines the sliding window duration.
    /// Must be greater than <see cref="Bucket"/>.
    /// </value>
    /// <remarks>
    /// <para>
    /// The window determines how far back in time the circuit breaker looks when calculating failure rates.
    /// A longer window provides more stable behavior but slower reaction to changing conditions.
    /// A shorter window reacts faster but may be more sensitive to temporary spikes.
    /// </para>
    /// <para>
    /// Typical values: 30-120 seconds for fast-changing services, 2-5 minutes for stable services.
    /// </para>
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan Window { get; }

    /// <summary>
    /// Gets the time duration of each bucket within the sliding window.
    /// </summary>
    /// <value>
    /// A <see cref="TimeSpan"/> between 1 second and 1 hour that defines individual bucket size.
    /// Must be less than <see cref="Window"/>.
    /// </value>
    /// <remarks>
    /// <para>
    /// Smaller buckets provide more granular failure tracking but require more memory and storage.
    /// The number of buckets is calculated as Window ÷ Bucket.
    /// </para>
    /// <para>
    /// Recommended: Set bucket to 1/6 to 1/12 of your window size. For a 60-second window,
    /// use 5-10 second buckets.
    /// </para>
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan Bucket { get; }

    /// <summary>
    /// Gets the minimum number of requests required in the window before the circuit breaker can open.
    /// </summary>
    /// <value>
    /// A positive integer representing the minimum sample size for statistical significance.
    /// </value>
    /// <remarks>
    /// <para>
    /// This prevents the circuit breaker from opening due to a few failures when traffic is low.
    /// For example, if MinSamples is 10 and only 5 requests occurred (all failures),
    /// the circuit breaker will not open despite a 100% failure rate.
    /// </para>
    /// <para>
    /// Set this based on your typical traffic patterns. For high-traffic services, use 50-100.
    /// For low-traffic services, use 5-20.
    /// </para>
    /// </remarks>
    [Range(1, int.MaxValue)]
    public int MinSamples { get; }

    /// <summary>
    /// Gets the failure rate threshold that triggers the circuit breaker to open.
    /// </summary>
    /// <value>
    /// A decimal value between 0.0 and 1.0, where 0.5 represents 50% failure rate.
    /// </value>
    /// <remarks>
    /// <para>
    /// When the failure rate within the observation window equals or exceeds this threshold
    /// (and MinSamples is met), the circuit breaker opens and redirects traffic to secondary endpoints.
    /// </para>
    /// <para>
    /// Common values:
    /// - 0.5 (50%) - Balanced approach, suitable for most services
    /// - 0.7 (70%) - More tolerant, allows more failures before opening
    /// - 0.3 (30%) - More aggressive, opens quickly on failures
    /// </para>
    /// </remarks>
    [Range(0.0, 1.0)]
    public double FailureRateToOpen { get; }

    /// <summary>
    /// Gets the duration the circuit breaker remains in the Open state before transitioning to Half-Open.
    /// </summary>
    /// <value>
    /// A <see cref="TimeSpan"/> that defines the cooling-off period during which all traffic goes to secondary endpoints.
    /// </value>
    /// <remarks>
    /// <para>
    /// During this period, the circuit breaker gives the primary endpoint time to recover
    /// from whatever issue caused the failures. No probe requests are sent to the primary.
    /// </para>
    /// <para>
    /// Balance recovery time with user experience:
    /// - Too short: May not allow sufficient recovery time
    /// - Too long: Users experience degraded service longer than necessary
    /// </para>
    /// <para>
    /// Typical values: 10-60 seconds depending on service recovery characteristics.
    /// </para>
    /// </remarks>
    public TimeSpan OpenCooldown { get; }

    /// <summary>
    /// Gets the maximum number of probe requests allowed simultaneously in the Half-Open state.
    /// </summary>
    /// <value>
    /// A positive integer that limits concurrent probes to prevent overwhelming a recovering service.
    /// </value>
    /// <remarks>
    /// <para>
    /// In Half-Open state, only a limited number of requests are sent to the primary endpoint
    /// as "probes" to test if it has recovered. Excess requests continue to the secondary endpoint.
    /// </para>
    /// <para>
    /// This prevents a flood of requests from hitting a service that may still be recovering.
    /// Recommended values: 1-5 for most scenarios. Higher values for high-throughput services
    /// that can handle more concurrent probes.
    /// </para>
    /// </remarks>
    [Range(1, int.MaxValue)]
    public int HalfOpenMaxProbes { get; }

    /// <summary>
    /// Gets the number of consecutive successful probe requests required to close the circuit breaker.
    /// </summary>
    /// <value>
    /// A positive integer representing the success threshold for transitioning from Half-Open to Closed.
    /// </value>
    /// <remarks>
    /// <para>
    /// The circuit breaker tracks consecutive successful probes. Once this threshold is reached,
    /// it transitions to Closed state and begins the recovery ramp-up process.
    /// A single failed probe resets the counter and returns the circuit breaker to Open state.
    /// </para>
    /// <para>
    /// Higher values provide more confidence that the service has recovered but delay full recovery.
    /// Lower values allow faster recovery but may be less reliable.
    /// </para>
    /// <para>
    /// Recommended: 3-10 depending on service reliability requirements.
    /// </para>
    /// </remarks>
    [Range(1, int.MaxValue)]
    public int HalfOpenSuccessesToClose { get; }

    /// <summary>
    /// Gets the recovery ramp profile that defines gradual traffic restoration.
    /// </summary>
    /// <value>
    /// A <see cref="RampProfile"/> that specifies percentage steps, hold duration, and failure tolerance during recovery.
    /// </value>
    /// <remarks>
    /// <para>
    /// When the circuit breaker closes, it doesn't immediately send 100% of traffic to the primary endpoint.
    /// Instead, it gradually increases traffic according to the ramp profile to prevent overwhelming
    /// a service that may still be partially impaired.
    /// </para>
    /// <para>
    /// The ramp monitors failure rates at each step. If failures exceed the configured threshold,
    /// the circuit breaker opens again, protecting the recovering service.
    /// </para>
    /// <para>
    /// Example ramp: [10, 25, 50, 75, 100] with 30-second holds means:
    /// - Step 1: 10% primary, 90% secondary for 30 seconds
    /// - Step 2: 25% primary, 75% secondary for 30 seconds
    /// - And so on until 100% primary traffic is restored
    /// </para>
    /// </remarks>
    [Required]
    public RampProfile Ramp { get; }

    /// <summary>
    /// Validates the circuit breaker configuration for logical consistency and business rule compliance.
    /// </summary>
    /// <param name="name">The name of the options instance being validated (may be null for default instance).</param>
    /// <param name="options">The options instance to validate.</param>
    /// <returns>
    /// <see cref="ValidateOptionsResult.Success"/> if the configuration is valid,
    /// or <see cref="ValidateOptionsResult.Fail(string)"/> with a descriptive error message if invalid.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method is automatically called during application startup when using the options pattern
    /// with dependency injection. Validation failures will prevent the application from starting.
    /// </para>
    /// <para>
    /// Validated rules:
    /// - Window must be greater than Bucket (prevents division by zero and ensures meaningful buckets)
    /// - Ramp must have at least one percentage step (prevents empty ramp configuration)
    /// </para>
    /// <para>
    /// Additional validation is provided by DataAnnotations attributes on individual properties.
    /// </para>
    /// </remarks>
    public ValidateOptionsResult Validate(string? name, CircuitBreakerOptions options)
    {
        if (options.Window <= options.Bucket)
        {
            return ValidateOptionsResult.Fail("Window must be greater than bucket size");
        }
        if (options.Ramp.Percentages.Length == 0)
        {
            return ValidateOptionsResult.Fail("Ramp percentages required");
        }
        return ValidateOptionsResult.Success;
    }
}
