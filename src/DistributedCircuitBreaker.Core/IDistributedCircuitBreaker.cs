namespace DistributedCircuitBreaker.Core;

/// <summary>
/// Provides a distributed circuit breaker abstraction for managing endpoint failover and recovery
/// across multiple application instances in a distributed environment.
/// </summary>
/// <remarks>
/// <para>
/// This interface defines the core operations of a distributed circuit breaker that can
/// automatically route traffic between primary and secondary endpoints based on failure rates
/// and recovery state. The circuit breaker operates in three states:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="BreakerState.Closed"/> - Normal operation, traffic goes to primary endpoint</description></item>
/// <item><description><see cref="BreakerState.Open"/> - Failure detected, all traffic goes to secondary endpoint</description></item>
/// <item><description><see cref="BreakerState.HalfOpen"/> - Recovery testing, limited probe traffic to primary</description></item>
/// </list>
/// <para>
/// The distributed nature means that multiple application instances can share the same circuit breaker
/// state through a backing store (typically Redis), ensuring consistent behavior across the cluster.
/// </para>
/// <para>
/// This interface is thread-safe and designed for high-concurrency scenarios. All operations are
/// asynchronous and support cancellation.
/// </para>
/// </remarks>
/// <example>
/// <para>Basic usage pattern:</para>
/// <code>
/// // Choose endpoint based on circuit breaker state
/// var choice = await breaker.ChooseAsync(primaryUri, secondaryUri, cancellationToken);
/// 
/// try
/// {
///     // Make request to chosen endpoint
///     var result = await httpClient.GetAsync(choice.Endpoint, cancellationToken);
///     
///     // Report success
///     await breaker.ReportAsync(result.IsSuccessStatusCode, choice.UseProbe, cancellationToken);
///     return result;
/// }
/// catch (Exception)
/// {
///     // Report failure
///     await breaker.ReportAsync(false, choice.UseProbe, cancellationToken);
///     throw;
/// }
/// </code>
/// </example>
public interface IDistributedCircuitBreaker
{
    /// <summary>
    /// Gets the current state of the circuit breaker.
    /// </summary>
    /// <value>
    /// The current <see cref="BreakerState"/> indicating whether the circuit is closed, open, or half-open.
    /// </value>
    /// <remarks>
    /// <para>
    /// This property reflects the local cache of the circuit breaker state. The actual authoritative
    /// state is maintained in the distributed store and may differ briefly due to network latency
    /// or synchronization delays.
    /// </para>
    /// <para>
    /// The state is automatically updated during <see cref="ChooseAsync"/> operations when the
    /// local cache is refreshed from the distributed store.
    /// </para>
    /// </remarks>
    BreakerState State { get; }

    /// <summary>
    /// Chooses between primary and secondary endpoints based on the current circuit breaker state
    /// and configured routing policies.
    /// </summary>
    /// <param name="primary">The primary endpoint URI to route to under normal conditions.</param>
    /// <param name="secondary">The secondary endpoint URI to route to during failures or recovery.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>
    /// A task that represents the asynchronous choose operation. The task result contains an
    /// <see cref="EndpointChoice"/> with the selected endpoint and routing metadata.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method implements the core routing logic of the circuit breaker:
    /// </para>
    /// <list type="bullet">
    /// <item><description><strong>Closed state:</strong> Returns primary endpoint (or probabilistic routing during ramp-up)</description></item>
    /// <item><description><strong>Open state:</strong> Returns secondary endpoint exclusively</description></item>
    /// <item><description><strong>Half-open state:</strong> Returns primary for limited probe requests, secondary for others</description></item>
    /// </list>
    /// <para>
    /// The method automatically synchronizes with the distributed store to ensure consistent
    /// state across all application instances. This may involve Redis lookups and should be
    /// called for every request that needs routing.
    /// </para>
    /// <para>
    /// During recovery ramp-up, the method uses probabilistic routing to gradually increase
    /// traffic to the primary endpoint based on the configured ramp profile.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="primary"/> or <paramref name="secondary"/> is null.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled via <paramref name="cancellationToken"/>.</exception>
    Task<EndpointChoice> ChooseAsync(Uri primary, Uri secondary, CancellationToken cancellationToken);

    /// <summary>
    /// Reports the result of a request to update failure statistics and trigger state transitions.
    /// </summary>
    /// <param name="success">
    /// <see langword="true"/> if the request was successful; <see langword="false"/> if it failed.
    /// Success is typically determined by HTTP status codes (2xx = success) or absence of exceptions.
    /// </param>
    /// <param name="wasProbe">
    /// <see langword="true"/> if this request was a probe request in half-open state;
    /// <see langword="false"/> for normal requests. This value comes from <see cref="EndpointChoice.UseProbe"/>.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>A task that represents the asynchronous report operation.</returns>
    /// <remarks>
    /// <para>
    /// This method is critical for the circuit breaker's operation as it:
    /// </para>
    /// <list type="number">
    /// <item><description>Records success/failure statistics in the distributed store</description></item>
    /// <item><description>Evaluates whether state transitions should occur</description></item>
    /// <item><description>Updates OpenTelemetry metrics for observability</description></item>
    /// <item><description>Manages recovery ramp-up progression</description></item>
    /// </list>
    /// <para>
    /// <strong>State Transition Logic:</strong>
    /// </para>
    /// <list type="bullet">
    /// <item><description><strong>Closed → Open:</strong> When failure rate exceeds threshold and minimum samples are met</description></item>
    /// <item><description><strong>Half-open → Closed:</strong> When consecutive probe successes reach the configured threshold</description></item>
    /// <item><description><strong>Half-open → Open:</strong> When any probe request fails</description></item>
    /// <item><description><strong>Ramp progression:</strong> When failure rate during recovery stays within acceptable limits</description></item>
    /// </list>
    /// <para>
    /// Always call this method after receiving a response (success or failure) to ensure
    /// accurate circuit breaker behavior. Failure to report results will prevent the
    /// circuit breaker from detecting failures or recovering properly.
    /// </para>
    /// </remarks>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled via <paramref name="cancellationToken"/>.</exception>
    Task ReportAsync(bool success, bool wasProbe, CancellationToken cancellationToken);
}

/// <summary>
/// Represents the result of an endpoint choice operation, containing the selected endpoint
/// and metadata about the routing decision.
/// </summary>
/// <param name="Endpoint">The URI of the endpoint that should be used for the request.</param>
/// <param name="UseProbe">
/// <see langword="true"/> if this request should be treated as a probe in half-open state;
/// <see langword="false"/> for normal requests. This value must be passed to <see cref="IDistributedCircuitBreaker.ReportAsync"/>
/// to ensure correct state transition logic.
/// </param>
/// <param name="PrimaryWeightPercent">
/// The percentage weight assigned to the primary endpoint during recovery ramp-up.
/// Values range from 0-100, where 0 means no traffic to primary, 100 means full traffic to primary.
/// This value is informational and useful for observability and debugging.
/// </param>
/// <remarks>
/// <para>
/// This record struct is returned by <see cref="IDistributedCircuitBreaker.ChooseAsync"/> and contains
/// all the information needed to route the request and properly report its outcome.
/// </para>
/// <para>
/// <strong>Important:</strong> The <see cref="UseProbe"/> value must be preserved and passed to
/// <see cref="IDistributedCircuitBreaker.ReportAsync"/> to ensure the circuit breaker can
/// correctly track probe requests vs. normal requests during recovery.
/// </para>
/// <para>
/// The <see cref="PrimaryWeightPercent"/> value is useful for:
/// </para>
/// <list type="bullet">
/// <item><description>Observability - understanding traffic distribution during recovery</description></item>
/// <item><description>Debugging - verifying ramp-up progression is working correctly</description></item>
/// <item><description>Metrics - tracking recovery progress in monitoring systems</description></item>
/// </list>
/// </remarks>
/// <example>
/// <para>Typical usage in an HTTP client:</para>
/// <code>
/// var choice = await circuitBreaker.ChooseAsync(primaryUri, secondaryUri, cancellationToken);
/// 
/// // Use the chosen endpoint
/// httpClient.BaseAddress = choice.Endpoint;
/// 
/// try
/// {
///     var response = await httpClient.GetAsync("/api/data", cancellationToken);
///     
///     // Report result with probe flag preserved
///     await circuitBreaker.ReportAsync(
///         response.IsSuccessStatusCode, 
///         choice.UseProbe,  // <- Important: preserve this value
///         cancellationToken);
///         
///     return response;
/// }
/// catch (Exception)
/// {
///     await circuitBreaker.ReportAsync(false, choice.UseProbe, cancellationToken);
///     throw;
/// }
/// </code>
/// </example>
public readonly record struct EndpointChoice(Uri Endpoint, bool UseProbe, int PrimaryWeightPercent);
