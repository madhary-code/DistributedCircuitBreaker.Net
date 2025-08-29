namespace DistributedCircuitBreaker.Core;

/// <summary>
/// Provides an abstraction for storing and retrieving distributed circuit breaker state
/// across multiple application instances in a cluster environment.
/// </summary>
/// <remarks>
/// <para>
/// This interface defines the storage operations required to maintain circuit breaker state
/// in a distributed system. Implementations typically use Redis or other distributed caches
/// to ensure state consistency across all application instances sharing the same circuit breaker key.
/// </para>
/// <para>
/// The store manages four types of data:
/// </para>
/// <list type="bullet">
/// <item><description><strong>Buckets:</strong> Time-series failure/success counts for sliding window calculations</description></item>
/// <item><description><strong>Latch:</strong> Current circuit breaker state (Closed/Open/HalfOpen) with TTL</description></item>
/// <item><description><strong>Probes:</strong> Semaphore for limiting concurrent probe requests in half-open state</description></item>
/// <item><description><strong>Ramp:</strong> Current recovery percentage and hold expiration for ramp-up</description></item>
/// </list>
/// <para>
/// <strong>Thread Safety:</strong> All implementations must be thread-safe and support concurrent
/// access from multiple threads and application instances. Operations should be atomic where
/// appropriate to prevent race conditions in state transitions.
/// </para>
/// <para>
/// <strong>Performance Considerations:</strong> These methods are called for every request routed
/// through the circuit breaker, so implementations should be optimized for low latency and high throughput.
/// Consider using connection pooling, pipelining, and efficient serialization.
/// </para>
/// </remarks>
/// <example>
/// <para>Typical usage pattern in circuit breaker implementation:</para>
/// <code>
/// // Record request outcome
/// await store.RecordAsync("service-key", success: true, DateTimeOffset.UtcNow, 
///                        window: TimeSpan.FromSeconds(60), bucket: TimeSpan.FromSeconds(10), cancellationToken);
/// 
/// // Read failure statistics
/// var (successes, failures) = await store.ReadWindowAsync("service-key", DateTimeOffset.UtcNow,
///                                                         window: TimeSpan.FromSeconds(60), bucket: TimeSpan.FromSeconds(10), cancellationToken);
/// 
/// // Check if circuit should open
/// var total = successes + failures;
/// if (total >= minSamples && (failures / (double)total) >= failureThreshold)
/// {
///     await store.SetLatchAsync("service-key", BreakerState.Open, TimeSpan.FromSeconds(30), cancellationToken);
/// }
/// </code>
/// </example>
public interface IClusterBreakerStore
{
    /// <summary>
    /// Records a request outcome (success or failure) in the distributed time-series storage
    /// for sliding window failure rate calculations.
    /// </summary>
    /// <param name="key">The circuit breaker key identifying which circuit this record belongs to.</param>
    /// <param name="success">
    /// <see langword="true"/> if the request was successful; <see langword="false"/> if it failed.
    /// Success is typically determined by HTTP status codes or absence of exceptions.
    /// </param>
    /// <param name="timestamp">
    /// The timestamp when the request completed. Used for time-bucket alignment in the sliding window.
    /// Should typically be <see cref="DateTimeOffset.UtcNow"/> unless replaying historical data.
    /// </param>
    /// <param name="window">
    /// The total sliding window duration for failure rate calculations. Used to set appropriate
    /// TTL on bucket data to prevent unbounded storage growth.
    /// </param>
    /// <param name="bucket">
    /// The bucket size within the sliding window. The timestamp is aligned to bucket boundaries
    /// to ensure consistent bucketing across distributed instances.
    /// </param>
    /// <param name="token">Cancellation token for the asynchronous operation.</param>
    /// <returns>A task representing the asynchronous record operation.</returns>
    /// <remarks>
    /// <para>
    /// This method implements the core data collection for circuit breaker failure detection.
    /// It must efficiently handle high request volumes and ensure data consistency across
    /// multiple application instances.
    /// </para>
    /// <para>
    /// <strong>Implementation Requirements:</strong>
    /// </para>
    /// <list type="bullet">
    /// <item><description>Bucket alignment: Timestamps must be aligned to bucket boundaries using integer division</description></item>
    /// <item><description>Atomic increments: Success and failure counts must be incremented atomically</description></item>
    /// <item><description>TTL management: Buckets should expire after window + bucket duration to prevent storage bloat</description></item>
    /// <item><description>High throughput: Optimize for concurrent writes from multiple instances</description></item>
    /// </list>
    /// <para>
    /// <strong>Bucket Key Strategy:</strong>
    /// </para>
    /// <para>
    /// Most implementations use keys like: <c>cb:{key}:b:{aligned_epoch}</c> where aligned_epoch
    /// is calculated as: <c>(timestamp.ToUnixTimeSeconds() / bucket_seconds) * bucket_seconds</c>
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty or when time parameters are invalid.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled via <paramref name="token"/>.</exception>
    Task RecordAsync(string key, bool success, DateTimeOffset timestamp, TimeSpan window, TimeSpan bucket, CancellationToken token);

    /// <summary>
    /// Reads success and failure counts from the sliding window for failure rate calculation.
    /// </summary>
    /// <param name="key">The circuit breaker key to read statistics for.</param>
    /// <param name="now">
    /// The current timestamp for window calculation. Typically <see cref="DateTimeOffset.UtcNow"/>.
    /// The window extends backward in time from this point.
    /// </param>
    /// <param name="window">
    /// The sliding window duration. Data older than <paramref name="now"/> minus this duration is ignored.
    /// </param>
    /// <param name="bucket">
    /// The bucket size for granular data retrieval. Determines how many individual buckets
    /// need to be read and aggregated.
    /// </param>
    /// <param name="token">Cancellation token for the asynchronous operation.</param>
    /// <returns>
    /// A task whose result contains a tuple with the total number of successful requests
    /// and failed requests within the specified window.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method aggregates success and failure counts across all time buckets within
    /// the sliding window to provide current failure rate statistics for circuit breaker decisions.
    /// </para>
    /// <para>
    /// <strong>Implementation Requirements:</strong>
    /// </para>
    /// <list type="bullet">
    /// <item><description>Window calculation: Must read all buckets from (now - window) to now</description></item>
    /// <item><description>Batch operations: Should use pipelining or batch operations for efficiency</description></item>
    /// <item><description>Missing buckets: Handle missing/expired buckets gracefully (treat as zero counts)</description></item>
    /// <item><description>Atomic reads: Ensure consistency when reading multiple buckets</description></item>
    /// </list>
    /// <para>
    /// <strong>Performance Optimization:</strong>
    /// </para>
    /// <para>
    /// For a 60-second window with 10-second buckets, this method needs to read 6 bucket keys.
    /// Implementations should use batch operations like Redis MGET or pipelining to minimize
    /// round trips and improve performance.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty or when time parameters are invalid.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled via <paramref name="token"/>.</exception>
    Task<(int Successes, int Failures)> ReadWindowAsync(string key, DateTimeOffset now, TimeSpan window, TimeSpan bucket, CancellationToken token);

    /// <summary>
    /// Reads the current circuit breaker state from the distributed latch.
    /// </summary>
    /// <param name="key">The circuit breaker key to read the state for.</param>
    /// <param name="token">Cancellation token for the asynchronous operation.</param>
    /// <returns>
    /// A task whose result contains the current <see cref="BreakerState"/> if set,
    /// or <see langword="null"/> if no state is stored (defaults to Closed).
    /// </returns>
    /// <remarks>
    /// <para>
    /// The latch represents the authoritative state of the circuit breaker across all
    /// application instances. When <see langword="null"/>, the circuit breaker should
    /// assume <see cref="BreakerState.Closed"/> state.
    /// </para>
    /// <para>
    /// <strong>TTL Behavior:</strong>
    /// </para>
    /// <list type="bullet">
    /// <item><description><strong>Open state:</strong> Has TTL equal to cooldown duration, automatically transitions to HalfOpen</description></item>
    /// <item><description><strong>HalfOpen state:</strong> May have TTL to prevent indefinite half-open state</description></item>
    /// <item><description><strong>Closed state:</strong> Typically no TTL (persists until explicitly changed)</description></item>
    /// </list>
    /// <para>
    /// Implementations should handle serialization/deserialization of the enum value efficiently,
    /// commonly using string representation for human readability in debugging.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled via <paramref name="token"/>.</exception>
    Task<BreakerState?> ReadLatchAsync(string key, CancellationToken token);

    /// <summary>
    /// Sets the circuit breaker state in the distributed latch with optional expiration.
    /// </summary>
    /// <param name="key">The circuit breaker key to set the state for.</param>
    /// <param name="state">The new circuit breaker state to store.</param>
    /// <param name="ttl">
    /// Optional time-to-live for the state. If specified, the state will automatically
    /// expire after this duration. If <see langword="null"/>, the state persists indefinitely.
    /// </param>
    /// <param name="token">Cancellation token for the asynchronous operation.</param>
    /// <returns>A task representing the asynchronous set operation.</returns>
    /// <remarks>
    /// <para>
    /// This method updates the authoritative circuit breaker state that coordinates
    /// behavior across all application instances. State changes trigger immediate
    /// updates to traffic routing decisions.
    /// </para>
    /// <para>
    /// <strong>TTL Usage Patterns:</strong>
    /// </para>
    /// <list type="bullet">
    /// <item><description><strong>Open → HalfOpen:</strong> Set Open state with TTL equal to cooldown duration</description></item>
    /// <item><description><strong>HalfOpen → Closed:</strong> Set Closed state with no TTL (permanent until next failure)</description></item>
    /// <item><description><strong>HalfOpen → Open:</strong> Set Open state with TTL to restart cooldown period</description></item>
    /// </list>
    /// <para>
    /// <strong>Consistency Requirements:</strong>
    /// </para>
    /// <para>
    /// State updates should be immediately visible to all application instances to ensure
    /// coordinated behavior. Consider using pub/sub notifications for real-time updates
    /// in addition to polling-based state checks.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled via <paramref name="token"/>.</exception>
    Task SetLatchAsync(string key, BreakerState state, TimeSpan? ttl, CancellationToken token);

    /// <summary>
    /// Attempts to acquire a probe token for testing the primary endpoint in half-open state.
    /// </summary>
    /// <param name="key">The circuit breaker key to acquire a probe token for.</param>
    /// <param name="maxProbes">
    /// The maximum number of concurrent probe requests allowed. If this limit is reached,
    /// the method returns <see langword="false"/> without blocking.
    /// </param>
    /// <param name="ttl">
    /// The time-to-live for the probe semaphore. If this is the first probe acquisition,
    /// the semaphore expires after this duration to prevent leaked tokens.
    /// </param>
    /// <param name="token">Cancellation token for the asynchronous operation.</param>
    /// <returns>
    /// A task whose result is <see langword="true"/> if a probe token was successfully acquired,
    /// or <see langword="false"/> if the maximum probe limit has been reached.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method implements a distributed semaphore to limit the number of concurrent
    /// probe requests sent to a potentially recovering primary endpoint. It prevents
    /// overwhelming the service during the critical half-open recovery phase.
    /// </para>
    /// <para>
    /// <strong>Implementation Pattern:</strong>
    /// </para>
    /// <list type="number">
    /// <item><description>Atomically increment the probe counter</description></item>
    /// <item><description>If counter equals 1, set TTL to prevent indefinite probe locks</description></item>
    /// <item><description>If counter exceeds maxProbes, decrement and return false</description></item>
    /// <item><description>Otherwise, return true (probe token acquired)</description></item>
    /// </list>
    /// <para>
    /// <strong>Race Condition Handling:</strong>
    /// </para>
    /// <para>
    /// Multiple instances may try to acquire probes simultaneously. The implementation
    /// must handle these race conditions atomically to ensure the probe limit is respected
    /// across all instances without coordination overhead.
    /// </para>
    /// <para>
    /// <strong>Token Management:</strong>
    /// </para>
    /// <para>
    /// Acquired probe tokens must be released via <see cref="ReleaseProbeAsync"/> after
    /// the probe request completes (success or failure) to allow subsequent probes.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty or <paramref name="maxProbes"/> is less than 1.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled via <paramref name="token"/>.</exception>
    Task<bool> TryAcquireProbeAsync(string key, int maxProbes, TimeSpan ttl, CancellationToken token);

    /// <summary>
    /// Releases a previously acquired probe token, allowing other instances to acquire probes.
    /// </summary>
    /// <param name="key">The circuit breaker key to release a probe token for.</param>
    /// <param name="token">Cancellation token for the asynchronous operation.</param>
    /// <returns>A task representing the asynchronous release operation.</returns>
    /// <remarks>
    /// <para>
    /// This method decrements the probe semaphore counter to allow additional probe requests.
    /// It should be called exactly once for each successful <see cref="TryAcquireProbeAsync"/>
    /// call, regardless of whether the probe request succeeded or failed.
    /// </para>
    /// <para>
    /// <strong>Usage Pattern:</strong>
    /// </para>
    /// <code>
    /// if (await store.TryAcquireProbeAsync(key, maxProbes, ttl, cancellationToken))
    /// {
    ///     try
    ///     {
    ///         // Send probe request to primary endpoint
    ///         var response = await httpClient.GetAsync(primaryEndpoint, cancellationToken);
    ///         var success = response.IsSuccessStatusCode;
    ///         await circuitBreaker.ReportAsync(success, wasProbe: true, cancellationToken);
    ///     }
    ///     finally
    ///     {
    ///         // Always release the probe token
    ///         await store.ReleaseProbeAsync(key, cancellationToken);
    ///     }
    /// }
    /// </code>
    /// <para>
    /// <strong>Error Handling:</strong>
    /// </para>
    /// <para>
    /// This method should be safe to call even if no probe token was acquired or if
    /// the semaphore has already been released. Implementations should handle these
    /// cases gracefully without throwing exceptions.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled via <paramref name="token"/>.</exception>
    Task ReleaseProbeAsync(string key, CancellationToken token);

    /// <summary>
    /// Reads the current recovery ramp percentage from the distributed store.
    /// </summary>
    /// <param name="key">The circuit breaker key to read the ramp percentage for.</param>
    /// <param name="token">Cancellation token for the asynchronous operation.</param>
    /// <returns>
    /// A task whose result contains the current ramp percentage (0-100) if active,
    /// or <see langword="null"/> if no ramp is currently active (assume 100% primary traffic).
    /// </returns>
    /// <remarks>
    /// <para>
    /// The ramp percentage determines what portion of traffic should be sent to the
    /// primary endpoint during recovery. This value is shared across all application
    /// instances to ensure coordinated ramp-up behavior.
    /// </para>
    /// <para>
    /// <strong>Return Value Interpretation:</strong>
    /// </para>
    /// <list type="bullet">
    /// <item><description><strong>null:</strong> No active ramp, send 100% traffic to primary (normal operation)</description></item>
    /// <item><description><strong>0:</strong> Send 0% traffic to primary (all traffic to secondary)</description></item>
    /// <item><description><strong>1-99:</strong> Send specified percentage to primary, remainder to secondary</description></item>
    /// <item><description><strong>100:</strong> Send 100% traffic to primary (ramp complete)</description></item>
    /// </list>
    /// <para>
    /// <strong>TTL Behavior:</strong>
    /// </para>
    /// <para>
    /// Ramp values typically have TTL equal to the ramp step hold duration. When the TTL
    /// expires, the circuit breaker should advance to the next ramp step or complete
    /// the ramp if at 100%.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled via <paramref name="token"/>.</exception>
    Task<int?> ReadRampAsync(string key, CancellationToken token);

    /// <summary>
    /// Sets the recovery ramp percentage with expiration for coordinated traffic ramp-up.
    /// </summary>
    /// <param name="key">The circuit breaker key to set the ramp percentage for.</param>
    /// <param name="percent">
    /// The percentage of traffic (0-100) to send to the primary endpoint during this ramp step.
    /// </param>
    /// <param name="ttl">
    /// The duration to maintain this ramp percentage before it expires and the next
    /// ramp step should be evaluated.
    /// </param>
    /// <param name="token">Cancellation token for the asynchronous operation.</param>
    /// <returns>A task representing the asynchronous set operation.</returns>
    /// <remarks>
    /// <para>
    /// This method coordinates traffic ramp-up across all application instances by
    /// setting a shared percentage value that determines routing decisions during recovery.
    /// </para>
    /// <para>
    /// <strong>Ramp Progression Pattern:</strong>
    /// </para>
    /// <list type="number">
    /// <item><description>Circuit breaker closes after successful probes</description></item>
    /// <item><description>Set ramp to first percentage (e.g., 10%) with hold duration TTL</description></item>
    /// <item><description>Monitor failure rate at this percentage</description></item>
    /// <item><description>When TTL expires, advance to next percentage if failure rate is acceptable</description></item>
    /// <item><description>Repeat until reaching 100% or failure rate exceeds threshold</description></item>
    /// </list>
    /// <para>
    /// <strong>Special Values:</strong>
    /// </para>
    /// <list type="bullet">
    /// <item><description><strong>0:</strong> Circuit just opened, no primary traffic during ramp reset</description></item>
    /// <item><description><strong>100:</strong> Ramp complete, normal operation restored</description></item>
    /// </list>
    /// <para>
    /// <strong>Failure Handling:</strong>
    /// </para>
    /// <para>
    /// If failures exceed the ramp threshold at any percentage, the ramp should be
    /// reset to 0% and the circuit breaker should return to Open state.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty or <paramref name="percent"/> is not between 0 and 100.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled via <paramref name="token"/>.</exception>
    Task SetRampAsync(string key, int percent, TimeSpan ttl, CancellationToken token);
}
