using System.Collections.Concurrent;
using DistributedCircuitBreaker.Core;

namespace DistributedCircuitBreaker.Tests.Unit;

/// <summary>Thread-safe in-memory store for unit tests.</summary>
public sealed class InMemoryClusterBreakerStore : IClusterBreakerStore
{
    private readonly ConcurrentDictionary<string, (int s, int f)> _buckets = new();
    private readonly ConcurrentDictionary<string, BreakerState> _latch = new();
    private int _probes;
    private int? _ramp;

    public Task RecordAsync(string key, bool success, DateTimeOffset timestamp, TimeSpan window, TimeSpan bucket, CancellationToken token)
    {
        var k = timestamp.ToUnixTimeSeconds() / (long)bucket.TotalSeconds;
        var dictKey = $"{key}:{k}";
        _buckets.AddOrUpdate(dictKey, success ? (1,0) : (0,1), (_, old) => success ? (old.s + 1, old.f) : (old.s, old.f + 1));
        return Task.CompletedTask;
    }

    public Task<(int Successes, int Failures)> ReadWindowAsync(string key, DateTimeOffset now, TimeSpan window, TimeSpan bucket, CancellationToken token)
    {
        int s = 0, f = 0;
        foreach (var kv in _buckets)
        {
            var parts = kv.Key.Split(':');
            if (parts[0] != key) continue;
            var epoch = long.Parse(parts[1]);
            if (epoch >= now.ToUnixTimeSeconds() - (long)window.TotalSeconds)
            {
                s += kv.Value.s; f += kv.Value.f;
            }
        }
        return Task.FromResult((s, f));
    }

    public Task<BreakerState?> ReadLatchAsync(string key, CancellationToken token)
        => Task.FromResult(_latch.TryGetValue(key, out var v) ? v : (BreakerState?)null);

    public Task SetLatchAsync(string key, BreakerState state, TimeSpan? ttl, CancellationToken token)
    {
        _latch[key] = state;
        return Task.CompletedTask;
    }

    public Task<bool> TryAcquireProbeAsync(string key, int maxProbes, TimeSpan ttl, CancellationToken token)
    {
        var val = Interlocked.Increment(ref _probes);
        if (val > maxProbes)
        {
            Interlocked.Decrement(ref _probes);
            return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }

    public Task ReleaseProbeAsync(string key, CancellationToken token)
    {
        Interlocked.Decrement(ref _probes);
        return Task.CompletedTask;
    }

    public Task<int?> ReadRampAsync(string key, CancellationToken token)
        => Task.FromResult(_ramp);

    public Task SetRampAsync(string key, int percent, TimeSpan ttl, CancellationToken token)
    {
        _ramp = percent;
        return Task.CompletedTask;
    }
}
