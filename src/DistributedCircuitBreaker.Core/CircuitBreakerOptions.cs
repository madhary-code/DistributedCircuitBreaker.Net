using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DistributedCircuitBreaker.Core;

/// <summary>Configuration for <see cref="DistributedCircuitBreaker"/>.</summary>
public sealed class CircuitBreakerOptions : IValidateOptions<CircuitBreakerOptions>
{
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

    [Required]
    public string Key { get; }
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan Window { get; }
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan Bucket { get; }
    [Range(1, int.MaxValue)]
    public int MinSamples { get; }
    [Range(0.0, 1.0)]
    public double FailureRateToOpen { get; }
    public TimeSpan OpenCooldown { get; }
    [Range(1, int.MaxValue)]
    public int HalfOpenMaxProbes { get; }
    [Range(1, int.MaxValue)]
    public int HalfOpenSuccessesToClose { get; }
    [Required]
    public RampProfile Ramp { get; }

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
