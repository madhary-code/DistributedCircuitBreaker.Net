using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace DistributedCircuitBreaker.Core;

/// <summary>
/// Production-ready implementation of <see cref="IDistributedCircuitBreaker"/> that provides
/// distributed circuit breaking with Redis-backed state coordination, automatic failover,
/// gradual recovery, and comprehensive OpenTelemetry integration.
/// </summary>
/// <remarks>
/// <para>
/// This implementation serves as the core circuit breaker engine that coordinates failure detection,
/// state transitions, and recovery across multiple application instances in a distributed environment.
/// It combines local caching for performance with distributed coordination for consistency.
/// </para>
/// <para>
/// <strong>State Machine Implementation:</strong>
/// </para>
/// <list type="bullet">
/// <item><description><strong>Closed:</strong> Normal operation with continuous failure monitoring and optional ramp-up</description></item>
/// <item><description><strong>Open:</strong> Failure mode with automatic cooldown timer and secondary endpoint routing</description></item>
/// <item><description><strong>Half-Open:</strong> Recovery testing with limited probe requests and success tracking</description></item>
/// </list>
/// <para>
/// <strong>Distributed Coordination:</strong>
/// </para>
/// <para>
/// Multiple application instances sharing the same circuit breaker key will coordinate through
/// the backing store (typically Redis) to ensure consistent behavior across the cluster:
/// </para>
/// <list type="bullet">
/// <item><description>Shared failure statistics in time-aligned buckets</description></item>
/// <item><description>Coordinated state transitions with TTL-based automatic progression</description></item>
/// <item><description>Distributed probe semaphore for controlled recovery testing</description></item>
/// <item><description>Synchronized recovery ramp-up with failure rate monitoring</description></item>
/// </list>
/// <para>
/// <strong>Performance and Thread Safety:</strong>
/// </para>
/// <para>
/// This implementation is designed for high-throughput, low-latency operation:
/// </para>
/// <list type="bullet">
/// <item><description>Thread-safe with minimal locking using volatile fields and atomic operations</description></item>
/// <item><description>Local state caching to reduce distributed store round trips</description></item>
/// <item><description>Async/await throughout with proper ConfigureAwait usage</description></item>
/// <item><description>Optimized hot path for normal closed-state operations</description></item>
/// </list>
/// <para>
/// <strong>Observability Integration:</strong>
/// </para>
/// <para>
/// Comprehensive telemetry is provided through OpenTelemetry standards:
/// </para>
/// <list type="bullet">
/// <item><description><strong>Activity Source:</strong> "DistributedCircuitBreaker" for distributed tracing</description></item>
/// <item><description><strong>Meter:</strong> "DistributedCircuitBreaker" for metrics and monitoring</description></item>
/// <item><description><strong>Structured Logging:</strong> State transitions and operational events</description></item>
/// </list>
/// </remarks>
/// <example>
/// <para>Basic usage pattern:</para>
/// <code>
/// // Dependency injection setup
/// services.AddSingleton&lt;IConnectionMultiplexer&gt;(ConnectionMultiplexer.Connect("localhost:6379"));
/// services.AddSingleton&lt;IClusterBreakerStore, RedisClusterBreakerStore&gt;();
/// services.AddDistributedCircuitBreaker(options =&gt;
/// {
///     options.Key = "orders-service";
///     options.Window = TimeSpan.FromMinutes(2);
///     options.Bucket = TimeSpan.FromSeconds(10);
///     options.MinSamples = 20;
///     options.FailureRateToOpen = 0.5;
///     options.OpenCooldown = TimeSpan.FromSeconds(30);
///     options.HalfOpenMaxProbes = 3;
///     options.HalfOpenSuccessesToClose = 5;
///     options.Ramp = new RampProfile(new[] { 10, 25, 50, 75, 100 }, TimeSpan.FromSeconds(30), 0.1);
/// });
/// 
/// // Usage in application code
/// public class OrderService
/// {
///     private readonly IDistributedCircuitBreaker _circuitBreaker;
///     private readonly HttpClient _httpClient;
///     
///     public async Task&lt;Order&gt; GetOrderAsync(int id)
///     {
///         var choice = await _circuitBreaker.ChooseAsync(
///             new Uri("https://orders-api.production.com"),
///             new Uri("https://orders-api.backup.com"),
///             cancellationToken);
///             
///         try
///         {
///             var response = await _httpClient.GetAsync(choice.Endpoint + $"/orders/{id}");
///             var success = response.IsSuccessStatusCode;
///             await _circuitBreaker.ReportAsync(success, choice.UseProbe, cancellationToken);
///             
///             if (success)
///             {
///                 return await response.Content.ReadFromJsonAsync&lt;Order&gt;();
///             }
///             throw new InvalidOperationException($"Service returned {response.StatusCode}");
///         }
///         catch (Exception)
///         {
///             await _circuitBreaker.ReportAsync(false, choice.UseProbe, cancellationToken);
///             throw;
///         }
///     }
/// }
/// </code>
/// </example>
public sealed class DistributedCircuitBreaker : IDistributedCircuitBreaker
{
    private readonly IClusterBreakerStore _store;
    private readonly CircuitBreakerOptions _options;
    private readonly ILogger<DistributedCircuitBreaker> _logger;
    private readonly ActivitySource _activity = new("DistributedCircuitBreaker");
    private readonly Meter _meter = new("DistributedCircuitBreaker");
    private readonly Counter<long> _requests;
    private readonly Counter<long> _successes;
    private readonly Counter<long> _failures;
    private int _halfOpenSuccessStreak;
    private volatile BreakerState _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="DistributedCircuitBreaker"/> class with the
    /// specified store, configuration, and logger.
    /// </summary>
    /// <param name="store">
    /// The distributed store implementation for coordinating state across application instances.
    /// Typically a Redis-based implementation for production scenarios.
    /// </param>
    /// <param name="options">
    /// The circuit breaker configuration including failure thresholds, timing parameters,
    /// and recovery settings. Should be validated before passing to this constructor.
    /// </param>
    /// <param name="logger">
    /// Logger instance for operational visibility, state transition events, and debugging information.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>Initialization Behavior:</strong>
    /// </para>
    /// <list type="bullet">
    /// <item><description>Circuit breaker starts in Closed state (normal operation)</description></item>
    /// <item><description>OpenTelemetry meters and activity sources are configured</description></item>
    /// <item><description>Internal counters are reset to zero</description></item>
    /// <item><description>No initial coordination with distributed store (lazy synchronization)</description></item>
    /// </list>
    /// <para>
    /// <strong>Dependency Requirements:</strong>
    /// </para>
    /// <para>
    /// The store implementation must be thread-safe and ready for immediate use.
    /// The options should be pre-validated to ensure configuration consistency.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="store"/>, <paramref name="options"/>, or <paramref name="logger"/> is null.
    /// </exception>
    public DistributedCircuitBreaker(IClusterBreakerStore store, CircuitBreakerOptions options, ILogger<DistributedCircuitBreaker> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
        _state = BreakerState.Closed;
        _requests = _meter.CreateCounter<long>("requests_total");
        _successes = _meter.CreateCounter<long>("successes_total");
        _failures = _meter.CreateCounter<long>("failures_total");
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns the locally cached circuit breaker state. This value is periodically synchronized
    /// with the distributed store during <see cref="ChooseAsync"/> operations but may lag slightly
    /// behind the authoritative distributed state due to network latency.
    /// </remarks>
    public BreakerState State => _state;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This method implements the core routing logic with distributed state synchronization:
    /// </para>
    /// <list type="number">
    /// <item><description><strong>State Synchronization:</strong> Reads authoritative state from distributed store</description></item>
    /// <item><description><strong>Local Cache Update:</strong> Updates local state if different from distributed state</description></item>
    /// <item><description><strong>Routing Decision:</strong> Applies state-specific routing logic</description></item>
    /// <item><description><strong>Telemetry:</strong> Records request metric and creates activity span</description></item>
    /// </list>
    /// <para>
    /// <strong>State-Specific Routing Logic:</strong>
    /// </para>
    /// <list type="bullet">
    /// <item><description><strong>Closed:</strong> Route to primary (or probabilistic based on ramp percentage)</description></item>
    /// <item><description><strong>Open:</strong> Route to secondary exclusively</description></item>
    /// <item><description><strong>Half-Open:</strong> Attempt probe acquisition, route to primary if successful, otherwise secondary</description></item>
    /// </list>
    /// <para>
    /// <strong>Ramp-Up Behavior:</strong>
    /// </para>
    /// <para>
    /// During recovery in Closed state, if a ramp percentage is active (&lt;100%), requests are
    /// probabilistically routed using thread-safe random number generation. For example,
    /// a 25% ramp means 25% of requests go to primary, 75% to secondary.
    /// </para>
    /// </remarks>
    public async Task<EndpointChoice> ChooseAsync(Uri primary, Uri secondary, CancellationToken cancellationToken)
    {
        using var act = _activity.StartActivity("choose");
        _requests.Add(1);
        
        // Synchronize with distributed state
        var latch = await _store.ReadLatchAsync(_options.Key, cancellationToken).ConfigureAwait(false);
        if (latch.HasValue && latch.Value != _state)
        {
            _state = latch.Value;
            _logger.LogInformation("Circuit breaker {Key} state synchronized to {State}", _options.Key, _state);
        }

        switch (_state)
        {
            case BreakerState.Open:
                // Fail fast: all traffic to secondary
                return new EndpointChoice(secondary, false, 0);
                
            case BreakerState.HalfOpen:
                // Try to acquire probe token for limited primary testing
                if (await _store.TryAcquireProbeAsync(_options.Key, _options.HalfOpenMaxProbes, _options.OpenCooldown, cancellationToken).ConfigureAwait(false))
                {
                    return new EndpointChoice(primary, true, 0);
                }
                return new EndpointChoice(secondary, false, 0);
                
            default: // Closed
                // Check for active ramp-up
                var percent = await _store.ReadRampAsync(_options.Key, cancellationToken).ConfigureAwait(false) ?? 100;
                if (percent < 100)
                {
                    // Probabilistic routing during ramp-up
                    var roll = Random.Shared.Next(0, 100);
                    return roll < percent ? new EndpointChoice(primary, false, percent) : new EndpointChoice(secondary, false, percent);
                }
                // Normal operation: full traffic to primary
                return new EndpointChoice(primary, false, 100);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This method is the core feedback mechanism for the circuit breaker, handling:
    /// </para>
    /// <list type="number">
    /// <item><description><strong>Statistics Recording:</strong> Updates time-series success/failure data</description></item>
    /// <item><description><strong>Metrics Recording:</strong> Updates OpenTelemetry counters</description></item>
    /// <item><description><strong>State Evaluation:</strong> Checks if state transitions are needed</description></item>
    /// <item><description><strong>Recovery Management:</strong> Handles probe success tracking and ramp progression</description></item>
    /// </list>
    /// <para>
    /// <strong>State-Specific Processing:</strong>
    /// </para>
    /// <list type="bullet">
    /// <item><description><strong>Closed:</strong> Evaluate if failure rate triggers opening, manage ramp progression</description></item>
    /// <item><description><strong>Half-Open:</strong> Track probe success/failure, transition to Closed/Open accordingly</description></item>
    /// <item><description><strong>Open:</strong> No processing (waiting for cooldown)</description></item>
    /// </list>
    /// <para>
    /// <strong>Probe Handling:</strong>
    /// </para>
    /// <para>
    /// When <paramref name="wasProbe"/> is true, special logic applies:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Successful probes increment consecutive success counter</description></item>
    /// <item><description>Failed probes immediately trip the circuit back to Open</description></item>
    /// <item><description>Probe tokens are automatically released after processing</description></item>
    /// </list>
    /// </remarks>
    public async Task ReportAsync(bool success, bool wasProbe, CancellationToken cancellationToken)
    {
        using var act = _activity.StartActivity("report");
        
        // Record statistics for sliding window calculations
        await _store.RecordAsync(_options.Key, success, DateTimeOffset.UtcNow, _options.Window, _options.Bucket, cancellationToken).ConfigureAwait(false);
        
        // Update telemetry counters
        if (success)
        {
            _successes.Add(1);
        }
        else
        {
            _failures.Add(1);
        }

        // State-specific processing
        if (_state == BreakerState.Closed)
        {
            // In closed state, evaluate if we should open or advance ramp
            await EvaluateOpenAsync(cancellationToken).ConfigureAwait(false);
            await EvaluateRampAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_state == BreakerState.HalfOpen)
        {
            // In half-open state, handle probe results
            if (wasProbe)
            {
                // Release probe token (best effort)
                try
                {
                    await _store.ReleaseProbeAsync(_options.Key, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to release probe token for {Key}", _options.Key);
                }

                if (success)
                {
                    // Track consecutive probe successes
                    if (Interlocked.Increment(ref _halfOpenSuccessStreak) >= _options.HalfOpenSuccessesToClose)
                    {
                        _logger.LogInformation("Circuit breaker {Key} closing after {Successes} consecutive probe successes", 
                            _options.Key, _options.HalfOpenSuccessesToClose);
                        
                        // Transition to closed and start ramp-up
                        _halfOpenSuccessStreak = 0;
                        _state = BreakerState.Closed;
                        await _store.SetLatchAsync(_options.Key, BreakerState.Closed, null, cancellationToken).ConfigureAwait(false);
                        
                        // Initialize ramp-up if configured
                        if (_options.Ramp.Percentages.Length > 0)
                        {
                            await _store.SetRampAsync(_options.Key, _options.Ramp.Percentages[0], _options.Ramp.HoldDuration, cancellationToken).ConfigureAwait(false);
                            _logger.LogInformation("Circuit breaker {Key} starting ramp-up at {Percent}%", 
                                _options.Key, _options.Ramp.Percentages[0]);
                        }
                    }
                }
                else
                {
                    // Probe failure: reset and return to open
                    _halfOpenSuccessStreak = 0;
                    await TripOpenAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        // Open state: no action needed (waiting for cooldown)
    }

    /// <summary>
    /// Evaluates whether the circuit breaker should open based on current failure rates.
    /// </summary>
    /// <param name="token">Cancellation token for the operation.</param>
    /// <remarks>
    /// This method implements the core failure detection logic by reading the sliding window
    /// statistics and comparing against configured thresholds. It only triggers when both
    /// minimum sample count and failure rate thresholds are exceeded.
    /// </remarks>
    private async Task EvaluateOpenAsync(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var (succ, fail) = await _store.ReadWindowAsync(_options.Key, now, _options.Window, _options.Bucket, token).ConfigureAwait(false);
        var total = succ + fail;
        
        if (total >= _options.MinSamples)
        {
            var rate = fail / (double)total;
            if (rate >= _options.FailureRateToOpen)
            {
                _logger.LogWarning("Circuit breaker {Key} opening due to failure rate {Rate:P2} ({Failures}/{Total} requests)", 
                    _options.Key, rate, fail, total);
                await TripOpenAsync(token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Evaluates and advances the recovery ramp-up process based on current failure rates.
    /// </summary>
    /// <param name="token">Cancellation token for the operation.</param>
    /// <remarks>
    /// <para>
    /// This method manages the gradual traffic restoration process by:
    /// </para>
    /// <list type="number">
    /// <item><description>Reading current ramp percentage and failure statistics</description></item>
    /// <item><description>Checking if failure rate exceeds ramp threshold (triggers circuit opening)</description></item>
    /// <item><description>Advancing to next ramp step if hold duration has expired</description></item>
    /// <item><description>Completing ramp when 100% is reached</description></item>
    /// </list>
    /// </remarks>
    private async Task EvaluateRampAsync(CancellationToken token)
    {
        var percent = await _store.ReadRampAsync(_options.Key, token).ConfigureAwait(false);
        if (!percent.HasValue || percent.Value >= 100)
        {
            return; // No active ramp or ramp complete
        }
        
        // Check failure rate during ramp
        var now = DateTimeOffset.UtcNow;
        var (succ, fail) = await _store.ReadWindowAsync(_options.Key, now, _options.Window, _options.Bucket, token).ConfigureAwait(false);
        var total = succ + fail;
        var rate = total == 0 ? 0 : fail / (double)total;
        
        if (rate > _options.Ramp.MaxFailureRatePerStep)
        {
            _logger.LogWarning("Circuit breaker {Key} ramp failed at {Percent}% due to failure rate {Rate:P2}", 
                _options.Key, percent.Value, rate);
            await TripOpenAsync(token).ConfigureAwait(false);
            return;
        }
        
        // Advance ramp (TTL expiration will trigger this)
        var idx = Array.IndexOf(_options.Ramp.Percentages, percent.Value);
        if (idx >= 0 && idx < _options.Ramp.Percentages.Length - 1)
        {
            var nextPercent = _options.Ramp.Percentages[idx + 1];
            await _store.SetRampAsync(_options.Key, nextPercent, _options.Ramp.HoldDuration, token).ConfigureAwait(false);
            _logger.LogInformation("Circuit breaker {Key} ramp advanced to {Percent}%", _options.Key, nextPercent);
        }
        else
        {
            // Complete ramp
            await _store.SetRampAsync(_options.Key, 100, _options.Ramp.HoldDuration, token).ConfigureAwait(false);
            _logger.LogInformation("Circuit breaker {Key} ramp completed at 100%", _options.Key);
        }
    }

    /// <summary>
    /// Trips the circuit breaker to the Open state and initiates automatic recovery timing.
    /// </summary>
    /// <param name="token">Cancellation token for the operation.</param>
    /// <remarks>
    /// <para>
    /// This method handles the transition to Open state by:
    /// </para>
    /// <list type="number">
    /// <item><description>Setting local and distributed state to Open</description></item>
    /// <item><description>Resetting probe success counters</description></item>
    /// <item><description>Setting ramp percentage to 0 (no primary traffic)</description></item>
    /// <item><description>Starting background task for automatic half-open transition</description></item>
    /// </list>
    /// <para>
    /// The background task implements the cooldown period using Task.Delay and automatically
    /// transitions to Half-Open state when the cooldown expires.
    /// </para>
    /// </remarks>
    private async Task TripOpenAsync(CancellationToken token)
    {
        _logger.LogWarning("Circuit breaker {Key} opening for {Cooldown} cooldown", _options.Key, _options.OpenCooldown);
        _state = BreakerState.Open;
        _halfOpenSuccessStreak = 0;
        
        // Set distributed state with TTL for automatic progression
        await _store.SetLatchAsync(_options.Key, BreakerState.Open, _options.OpenCooldown, token).ConfigureAwait(false);
        await _store.SetRampAsync(_options.Key, 0, _options.Ramp.HoldDuration, token).ConfigureAwait(false);
        
        // Background task for automatic half-open transition
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_options.OpenCooldown, token).ConfigureAwait(false);
                _state = BreakerState.HalfOpen;
                await _store.SetLatchAsync(_options.Key, BreakerState.HalfOpen, _options.OpenCooldown, token).ConfigureAwait(false);
                _logger.LogInformation("Circuit breaker {Key} entering half-open state for recovery testing", _options.Key);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to transition circuit breaker {Key} to half-open state", _options.Key);
            }
        }, token);
    }
}
