namespace DistributedCircuitBreaker.Core;

/// <summary>Distributed breaker abstraction.</summary>
public interface IDistributedCircuitBreaker
{
    BreakerState State { get; }
    Task<EndpointChoice> ChooseAsync(Uri primary, Uri secondary, CancellationToken cancellationToken);
    Task ReportAsync(bool success, bool wasProbe, CancellationToken cancellationToken);
}

/// <summary>Choice result.</summary>
/// <param name="Endpoint">Chosen endpoint.</param>
/// <param name="UseProbe">Whether call was probe.</param>
/// <param name="PrimaryWeightPercent">Primary percentage weight.</param>
public readonly record struct EndpointChoice(Uri Endpoint, bool UseProbe, int PrimaryWeightPercent);
