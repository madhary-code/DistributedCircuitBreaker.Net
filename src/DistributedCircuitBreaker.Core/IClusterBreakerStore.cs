namespace DistributedCircuitBreaker.Core;

/// <summary>Store for cluster-wide breaker state.</summary>
public interface IClusterBreakerStore
{
    Task RecordAsync(string key, bool success, DateTimeOffset timestamp, TimeSpan window, TimeSpan bucket, CancellationToken token);
    Task<(int Successes, int Failures)> ReadWindowAsync(string key, DateTimeOffset now, TimeSpan window, TimeSpan bucket, CancellationToken token);
    Task<BreakerState?> ReadLatchAsync(string key, CancellationToken token);
    Task SetLatchAsync(string key, BreakerState state, TimeSpan? ttl, CancellationToken token);
    Task<bool> TryAcquireProbeAsync(string key, int maxProbes, TimeSpan ttl, CancellationToken token);
    Task ReleaseProbeAsync(string key, CancellationToken token);
    Task<int?> ReadRampAsync(string key, CancellationToken token);
    Task SetRampAsync(string key, int percent, TimeSpan ttl, CancellationToken token);
}
