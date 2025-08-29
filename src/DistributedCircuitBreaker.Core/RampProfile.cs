namespace DistributedCircuitBreaker.Core;

/// <summary>Defines ramp percentages for recovery.</summary>
/// <param name="Percentages">Percent steps 0..100.</param>
/// <param name="HoldDuration">Duration of each step.</param>
/// <param name="MaxFailureRatePerStep">Maximum failure rate allowed per step.</param>
public sealed record RampProfile(int[] Percentages, TimeSpan HoldDuration, double MaxFailureRatePerStep);
