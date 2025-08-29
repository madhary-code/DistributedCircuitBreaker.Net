namespace DistributedCircuitBreaker.Core;

/// <summary>
/// Defines the recovery ramp-up profile that controls gradual traffic restoration
/// to the primary endpoint after a circuit breaker closes following a failure period.
/// </summary>
/// <param name="Percentages">
/// Array of percentage values (0-100) representing traffic weight steps during recovery.
/// Each value defines what percentage of traffic should be sent to the primary endpoint
/// at each stage of the ramp-up process.
/// </param>
/// <param name="HoldDuration">
/// The duration to maintain each percentage level before advancing to the next step.
/// This allows time to evaluate the primary endpoint's stability at each traffic level.
/// </param>
/// <param name="MaxFailureRatePerStep">
/// The maximum allowed failure rate (0.0 to 1.0) during any ramp step.
/// If the failure rate exceeds this threshold, the circuit breaker will immediately
/// open again to protect the recovering primary endpoint.
/// </param>
/// <remarks>
/// <para>
/// The ramp profile implements a critical safety mechanism that prevents overwhelming
/// a recovering service with full traffic immediately after a circuit breaker closes.
/// Instead, it gradually increases load while monitoring for signs of continued instability.
/// </para>
/// <para>
/// <strong>Ramp Progression Example:</strong>
/// </para>
/// <para>
/// For a profile with percentages [10, 25, 50, 75, 100] and 30-second hold duration:
/// </para>
/// <list type="number">
/// <item><description>Step 1: 10% primary, 90% secondary for 30 seconds</description></item>
/// <item><description>Step 2: 25% primary, 75% secondary for 30 seconds</description></item>
/// <item><description>Step 3: 50% primary, 50% secondary for 30 seconds</description></item>
/// <item><description>Step 4: 75% primary, 25% secondary for 30 seconds</description></item>
/// <item><description>Step 5: 100% primary, 0% secondary (full recovery)</description></item>
/// </list>
/// <para>
/// <strong>Failure Protection:</strong>
/// </para>
/// <para>
/// At each step, if the failure rate exceeds <see cref="MaxFailureRatePerStep"/>,
/// the circuit breaker immediately transitions back to Open state, protecting the
/// primary endpoint from further load and restarting the recovery process.
/// </para>
/// <para>
/// <strong>Design Considerations:</strong>
/// </para>
/// <list type="bullet">
/// <item><description><strong>Percentage Steps:</strong> Should be gradual enough to detect instability but not so slow as to prolong degraded service</description></item>
/// <item><description><strong>Hold Duration:</strong> Must be long enough to collect meaningful failure statistics but short enough for responsive recovery</description></item>
/// <item><description><strong>Failure Threshold:</strong> Should be more conservative than the main circuit breaker threshold to catch early signs of continued issues</description></item>
/// </list>
/// </remarks>
/// <example>
/// <para>Conservative ramp profile for critical services:</para>
/// <code>
/// var conservativeRamp = new RampProfile(
///     percentages: new[] { 5, 10, 25, 50, 75, 100 },  // Very gradual increase
///     holdDuration: TimeSpan.FromSeconds(60),          // Longer evaluation periods
///     maxFailureRatePerStep: 0.1                       // Only 10% failures allowed
/// );
/// </code>
/// <para>Aggressive ramp profile for resilient services:</para>
/// <code>
/// var aggressiveRamp = new RampProfile(
///     percentages: new[] { 25, 50, 100 },              // Faster recovery
///     holdDuration: TimeSpan.FromSeconds(15),          // Shorter evaluation periods
///     maxFailureRatePerStep: 0.2                       // Allow 20% failures
/// );
/// </code>
/// <para>Single-step ramp for simple failback:</para>
/// <code>
/// var simpleRamp = new RampProfile(
///     percentages: new[] { 100 },                      // Immediate full recovery
///     holdDuration: TimeSpan.FromSeconds(1),           // Minimal hold time
///     maxFailureRatePerStep: 0.05                      // Very low failure tolerance
/// );
/// </code>
/// </example>
public sealed record RampProfile(int[] Percentages, TimeSpan HoldDuration, double MaxFailureRatePerStep)
{
    /// <summary>
    /// Gets the array of percentage values representing traffic weight steps during recovery.
    /// </summary>
    /// <value>
    /// An array of integers where each value is between 0 and 100, representing the percentage
    /// of traffic that should be sent to the primary endpoint at each stage of recovery.
    /// </value>
    /// <remarks>
    /// <para>
    /// The array defines the progression of traffic restoration:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Values must be between 0 and 100 inclusive</description></item>
    /// <item><description>Values typically increase monotonically (though not required)</description></item>
    /// <item><description>The final value is usually 100 to represent full recovery</description></item>
    /// <item><description>Empty array disables ramp-up (immediate 100% restoration)</description></item>
    /// </list>
    /// <para>
    /// Each percentage represents the probability that a given request will be routed
    /// to the primary endpoint, with the remainder going to the secondary endpoint.
    /// </para>
    /// </remarks>
    public int[] Percentages { get; } = Percentages;

    /// <summary>
    /// Gets the duration to maintain each percentage level before advancing to the next step.
    /// </summary>
    /// <value>
    /// A <see cref="TimeSpan"/> that defines how long to hold each traffic percentage level
    /// during the ramp-up process.
    /// </value>
    /// <remarks>
    /// <para>
    /// This duration serves multiple purposes:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Allows sufficient time to collect failure statistics at each level</description></item>
    /// <item><description>Provides stability during recovery to avoid rapid state changes</description></item>
    /// <item><description>Gives the primary endpoint time to warm up at each traffic level</description></item>
    /// </list>
    /// <para>
    /// Considerations for setting this value:
    /// </para>
    /// <list type="bullet">
    /// <item><description><strong>Too short:</strong> May not collect enough samples for reliable failure rate calculation</description></item>
    /// <item><description><strong>Too long:</strong> Prolongs degraded service during recovery</description></item>
    /// <item><description><strong>Typical range:</strong> 15-120 seconds depending on traffic volume and service characteristics</description></item>
    /// </list>
    /// </remarks>
    public TimeSpan HoldDuration { get; } = HoldDuration;

    /// <summary>
    /// Gets the maximum allowed failure rate during any ramp step before triggering circuit breaker reopening.
    /// </summary>
    /// <value>
    /// A decimal value between 0.0 and 1.0 representing the failure rate threshold
    /// that will cause the circuit breaker to reopen during recovery.
    /// </value>
    /// <remarks>
    /// <para>
    /// This threshold acts as a safety mechanism during recovery ramp-up:
    /// </para>
    /// <list type="bullet">
    /// <item><description>If failure rate exceeds this value at any ramp step, circuit immediately opens</description></item>
    /// <item><description>Should typically be lower than the main circuit breaker threshold</description></item>
    /// <item><description>Provides early detection of continued primary endpoint issues</description></item>
    /// <item><description>Prevents partial recovery from causing further damage</description></item>
    /// </list>
    /// <para>
    /// <strong>Recommended Values:</strong>
    /// </para>
    /// <list type="bullet">
    /// <item><description><strong>0.05-0.15 (5-15%):</strong> Conservative, for critical services that can't tolerate many failures</description></item>
    /// <item><description><strong>0.15-0.25 (15-25%):</strong> Balanced, for most production services</description></item>
    /// <item><description><strong>0.25-0.4 (25-40%):</strong> Permissive, for services with natural variability</description></item>
    /// </list>
    /// <para>
    /// The value should be set based on:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Service reliability requirements</description></item>
    /// <item><description>Acceptable user experience during recovery</description></item>
    /// <item><description>Natural error rates of the primary endpoint</description></item>
    /// <item><description>Cost of false positives vs. continued failures</description></item>
    /// </list>
    /// </remarks>
    public double MaxFailureRatePerStep { get; } = MaxFailureRatePerStep;
}
