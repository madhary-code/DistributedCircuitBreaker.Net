using DistributedCircuitBreaker.Core;
using StackExchange.Redis;

namespace DistributedCircuitBreaker.Redis;

/// <summary>Redis implementation of <see cref="IClusterBreakerStore"/>.</summary>
public sealed class RedisClusterBreakerStore : IClusterBreakerStore
{
    private readonly IDatabase _db;

    public RedisClusterBreakerStore(IConnectionMultiplexer mux)
        => _db = mux.GetDatabase();

    private static string BucketKey(string key, long epoch) => $"cb:{key}:b:{epoch}";
    private static string LatchKey(string key) => $"cb:{key}:latch";
    private static string ProbeKey(string key) => $"cb:{key}:probes";
    private static string RampKey(string key) => $"cb:{key}:ramp";

    public async Task RecordAsync(string key, bool success, DateTimeOffset timestamp, TimeSpan window, TimeSpan bucket, CancellationToken token)
    {
        var bucketSeconds = (long)bucket.TotalSeconds;
        var aligned = (timestamp.ToUnixTimeSeconds() / bucketSeconds) * bucketSeconds;
        var redisKey = BucketKey(key, aligned);
        var field = success ? "s" : "f";
        var tasks = new Task[]
        {
            _db.HashIncrementAsync(redisKey, field, 1),
            _db.KeyExpireAsync(redisKey, window + bucket)
        };
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task<(int Successes, int Failures)> ReadWindowAsync(string key, DateTimeOffset now, TimeSpan window, TimeSpan bucket, CancellationToken token)
    {
        var bucketSeconds = (long)bucket.TotalSeconds;
        var start = now.ToUnixTimeSeconds() - (long)window.TotalSeconds;
        var keys = new List<RedisKey>();
        for (var epoch = (start / bucketSeconds) * bucketSeconds; epoch <= now.ToUnixTimeSeconds(); epoch += bucketSeconds)
        {
            keys.Add(BucketKey(key, epoch));
        }
        var batch = _db.CreateBatch();
        var tasks = keys.Select(k => batch.HashGetAllAsync(k)).ToArray();
        batch.Execute();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        int s = 0, f = 0;
        foreach (var t in tasks)
        {
            foreach (var entry in t.Result)
            {
                if (entry.Name == "s") s += (int)entry.Value;
                else if (entry.Name == "f") f += (int)entry.Value;
            }
        }
        return (s, f);
    }

    public async Task<BreakerState?> ReadLatchAsync(string key, CancellationToken token)
    {
        var val = await _db.StringGetAsync(LatchKey(key)).ConfigureAwait(false);
        return val.IsNull ? null : Enum.Parse<BreakerState>(val!);
    }

    public Task SetLatchAsync(string key, BreakerState state, TimeSpan? ttl, CancellationToken token)
        => _db.StringSetAsync(LatchKey(key), state.ToString(), ttl);

    public async Task<bool> TryAcquireProbeAsync(string key, int maxProbes, TimeSpan ttl, CancellationToken token)
    {
        var keyName = ProbeKey(key);
        var val = await _db.StringIncrementAsync(keyName).ConfigureAwait(false);
        if (val == 1)
        {
            await _db.KeyExpireAsync(keyName, ttl).ConfigureAwait(false);
        }
        if (val > maxProbes)
        {
            await _db.StringDecrementAsync(keyName).ConfigureAwait(false);
            return false;
        }
        return true;
    }

    public Task ReleaseProbeAsync(string key, CancellationToken token)
        => _db.StringDecrementAsync(ProbeKey(key));

    public async Task<int?> ReadRampAsync(string key, CancellationToken token)
    {
        var val = await _db.StringGetAsync(RampKey(key)).ConfigureAwait(false);
        return val.IsNull ? null : (int)val;
    }

    public Task SetRampAsync(string key, int percent, TimeSpan ttl, CancellationToken token)
        => _db.StringSetAsync(RampKey(key), percent, ttl);
}
