namespace DistributedCircuitBreaker.Core;

/// <summary>
/// Represents the operational state of a distributed circuit breaker, determining how traffic
/// is routed between primary and secondary endpoints.
/// </summary>
/// <remarks>
/// <para>
/// The circuit breaker operates as a finite state machine with three distinct states,
/// each with specific behaviors for traffic routing and failure handling:
/// </para>
/// <list type="bullet">
/// <item><description><strong>Closed (0):</strong> Normal operation - traffic flows to primary endpoint</description></item>
/// <item><description><strong>Open (1):</strong> Failure mode - all traffic redirected to secondary endpoint</description></item>
/// <item><description><strong>HalfOpen (2):</strong> Recovery mode - limited probe traffic to test primary endpoint</description></item>
/// </list>
/// <para>
/// State transitions are triggered by failure rates, probe results, and configured timeouts.
/// The distributed nature means all application instances sharing the same circuit breaker
/// key will coordinate state changes through the backing store (typically Redis).
/// </para>
/// <para>
/// The numeric values (0, 1, 2) are significant for serialization to Redis and maintaining
/// compatibility across different application versions.
/// </para>
/// </remarks>
/// <example>
/// <para>State transition example:</para>
/// <code>
/// // Initial state - normal operation
/// breaker.State == BreakerState.Closed
/// 
/// // After failure threshold exceeded
/// breaker.State == BreakerState.Open
/// 
/// // After cooldown period expires
/// breaker.State == BreakerState.HalfOpen
/// 
/// // After successful probe requests
/// breaker.State == BreakerState.Closed  // Recovery complete
/// </code>
/// </example>
public enum BreakerState
{
    /// <summary>
    /// The circuit breaker is closed, allowing normal traffic flow to the primary endpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In this state:
    /// </para>
    /// <list type="bullet">
    /// <item><description>All traffic is routed to the primary endpoint (unless ramp-up is active)</description></item>
    /// <item><description>Failure statistics are continuously monitored</description></item>
    /// <item><description>If failure rate exceeds threshold, transitions to <see cref="Open"/></description></item>
    /// <item><description>During recovery ramp-up, traffic is gradually increased to primary</description></item>
    /// </list>
    /// <para>
    /// This is the default and preferred state, indicating the primary endpoint is healthy
    /// and operating normally.
    /// </para>
    /// </remarks>
    Closed = 0,

    /// <summary>
    /// The circuit breaker is open, redirecting all traffic to the secondary endpoint
    /// to allow the primary endpoint time to recover.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In this state:
    /// </para>
    /// <list type="bullet">
    /// <item><description>All traffic is routed to the secondary endpoint</description></item>
    /// <item><description>No requests are sent to the primary endpoint</description></item>
    /// <item><description>The system waits for the configured cooldown period</description></item>
    /// <item><description>After cooldown expires, transitions to <see cref="HalfOpen"/></description></item>
    /// </list>
    /// <para>
    /// This state is triggered when the failure rate exceeds the configured threshold
    /// within the observation window, indicating the primary endpoint is experiencing
    /// issues and needs protection from additional load.
    /// </para>
    /// <para>
    /// The duration in this state is controlled by the <c>OpenCooldown</c> configuration,
    /// which should provide sufficient time for the primary endpoint to recover from
    /// whatever issue caused the failures.
    /// </para>
    /// </remarks>
    Open = 1,

    /// <summary>
    /// The circuit breaker is half-open, testing the primary endpoint's recovery
    /// by sending limited probe requests while continuing to route most traffic
    /// to the secondary endpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In this state:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Limited probe requests are sent to the primary endpoint</description></item>
    /// <item><description>Excess traffic continues to the secondary endpoint</description></item>
    /// <item><description>Probe results determine the next state transition</description></item>
    /// <item><description>Successful probes may lead to <see cref="Closed"/> state</description></item>
    /// <item><description>Failed probes immediately return to <see cref="Open"/> state</description></item>
    /// </list>
    /// <para>
    /// This state represents a cautious approach to recovery, allowing the system to
    /// test whether the primary endpoint has recovered without overwhelming it with
    /// full traffic immediately.
    /// </para>
    /// <para>
    /// The number of concurrent probe requests is limited by the <c>HalfOpenMaxProbes</c>
    /// configuration, and the number of consecutive successful probes required to
    /// close the circuit is controlled by <c>HalfOpenSuccessesToClose</c>.
    /// </para>
    /// <para>
    /// If any probe request fails, the circuit immediately returns to <see cref="Open"/>
    /// state, resetting the recovery process to protect the still-recovering primary endpoint.
    /// </para>
    /// </remarks>
    HalfOpen = 2,
}
