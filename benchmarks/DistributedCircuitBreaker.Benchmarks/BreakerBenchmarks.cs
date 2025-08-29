using BenchmarkDotNet.Attributes;
using DistributedCircuitBreaker.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace DistributedCircuitBreaker.Benchmarks;

[MemoryDiagnoser]
public class BreakerBenchmarks
{
    private readonly IDistributedCircuitBreaker _breaker;

    public BreakerBenchmarks()
    {
        var store = new InMemoryClusterBreakerStore();
        var options = new CircuitBreakerOptions("bench", TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10), 10, 0.5, TimeSpan.FromSeconds(5), 1, 1, new RampProfile(new[]{100}, TimeSpan.FromSeconds(1), 1));
        _breaker = new Core.DistributedCircuitBreaker(store, options, NullLogger<Core.DistributedCircuitBreaker>.Instance);
    }

    [Benchmark]
    public async Task ChooseReport()
    {
        var choice = await _breaker.ChooseAsync(new("http://p"), new("http://s"), default);
        await _breaker.ReportAsync(true, choice.UseProbe, default);
    }
}

// Simple in-memory implementation for benchmarking
internal class InMemoryClusterBreakerStore : IClusterBreakerStore
{
    private readonly Dictionary<string, (int success, int failure)> _buckets = new();
    private readonly Dictionary<string, BreakerState> _latches = new();
    private readonly Dictionary<string, int> _probes = new();
    private readonly Dictionary<string, int> _ramps = new();

    public Task RecordAsync(string key, bool success, DateTimeOffset timestamp, TimeSpan window, TimeSpan bucket, CancellationToken token)
    {
        var bucketKey = $"{key}:{timestamp.ToUnixTimeSeconds() / (int)bucket.TotalSeconds}";
        if (_buckets.ContainsKey(bucketKey))
        {
            var (s, f) = _buckets[bucketKey];
            _buckets[bucketKey] = success ? (s + 1, f) : (s, f + 1);
        }
        else
        {
            _buckets[bucketKey] = success ? (1, 0) : (0, 1);
        }
        return Task.CompletedTask;
    }

    public Task<(int Successes, int Failures)> ReadWindowAsync(string key, DateTimeOffset now, TimeSpan window, TimeSpan bucket, CancellationToken token)
    {
        var cutoff = now.Subtract(window);
        int totalSuccess = 0, totalFailure = 0;
        foreach (var kvp in _buckets)
        {
            if (kvp.Key.StartsWith(key + ":"))
            {
                totalSuccess += kvp.Value.success;
                totalFailure += kvp.Value.failure;
            }
        }
        return Task.FromResult((totalSuccess, totalFailure));
    }

    public Task<BreakerState?> ReadLatchAsync(string key, CancellationToken token)
    {
        return Task.FromResult<BreakerState?>(_latches.TryGetValue(key, out var state) ? state : null);
    }

    public Task SetLatchAsync(string key, BreakerState state, TimeSpan? ttl, CancellationToken token)
    {
        _latches[key] = state;
        return Task.CompletedTask;
    }

    public Task<bool> TryAcquireProbeAsync(string key, int maxProbes, TimeSpan ttl, CancellationToken token)
    {
        var current = _probes.GetValueOrDefault(key, 0);
        if (current < maxProbes)
        {
            _probes[key] = current + 1;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task ReleaseProbeAsync(string key, CancellationToken token)
    {
        if (_probes.ContainsKey(key) && _probes[key] > 0)
        {
            _probes[key]--;
        }
        return Task.CompletedTask;
    }

    public Task<int?> ReadRampAsync(string key, CancellationToken token)
    {
        return Task.FromResult<int?>(_ramps.TryGetValue(key, out var ramp) ? ramp : null);
    }

    public Task SetRampAsync(string key, int percent, TimeSpan ttl, CancellationToken token)
    {
        _ramps[key] = percent;
        return Task.CompletedTask;
    }
}
