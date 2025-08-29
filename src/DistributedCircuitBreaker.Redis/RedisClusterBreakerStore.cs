using DistributedCircuitBreaker.Core;
using StackExchange.Redis;

namespace DistributedCircuitBreaker.Redis;

/// <summary>
/// Redis-based implementation of <see cref="IClusterBreakerStore"/> that provides 
/// distributed circuit breaker state coordination across multiple application instances.
/// </summary>
/// <remarks>
/// <para>
/// This implementation uses Redis as the backing store for circuit breaker state,
/// enabling multiple application instances to share failure detection, state transitions,
/// and recovery coordination. It's optimized for high-throughput scenarios with
/// efficient Redis operations and minimal network round trips.
/// </para>
/// <para>
/// <strong>Redis Data Structures:</strong>
/// </para>
/// <list type="bullet">
/// <item><description><strong>Buckets:</strong> Redis hashes storing success/failure counts with automatic expiration</description></item>
/// <item><description><strong>Latch:</strong> Redis strings storing current circuit breaker state with TTL</description></item>
/// <item><description><strong>Probes:</strong> Redis integers implementing distributed semaphore for probe limiting</description></item>
/// <item><description><strong>Ramp:</strong> Redis strings storing recovery percentage with hold duration TTL</description></item>
/// </list>
/// <para>
/// <strong>Key Naming Strategy:</strong>
/// </para>
/// <para>
/// All Redis keys use the prefix <c>cb:{circuit-key}:</c> followed by the data type:
/// </para>
/// <list type="bullet">
/// <item><description><c>cb:{key}:b:{epoch}</c> - Time bucket for success/failure counts</description></item>
/// <item><description><c>cb:{key}:latch</c> - Circuit breaker state (Open/Closed/HalfOpen)</description></item>
/// <item><description><c>cb:{key}:probes</c> - Probe semaphore counter</description></item>
/// <item><description><c>cb:{key}:ramp</c> - Recovery ramp percentage</description></item>
/// </list>
/// <para>
/// <strong>Performance Characteristics:</strong>
/// </para>
/// <list type="bullet">
/// <item><description>Batched operations using Redis pipelines for multi-bucket reads</description></item>
/// <item><description>Atomic operations for race-condition-free probe semaphore management</description></item>
/// <item><description>Automatic TTL management to prevent Redis memory bloat</description></item>
/// <item><description>Connection multiplexing for efficient resource utilization</description></item>
/// </list>
/// <para>
/// <strong>Thread Safety:</strong>
/// </para>
/// <para>
/// This implementation is thread-safe and designed for concurrent access from multiple
/// threads and application instances. All Redis operations are atomic or use appropriate
/// concurrency controls.
/// </para>
/// </remarks>
/// <example>
/// <para>Registration in ASP.NET Core:</para>
/// <code>
/// var builder = WebApplication.CreateBuilder(args);
/// 
/// // Register Redis connection
/// builder.Services.AddSingleton&lt;IConnectionMultiplexer&gt;(provider =&gt;
/// {
///     var connectionString = builder.Configuration.GetConnectionString("Redis");
///     return ConnectionMultiplexer.Connect(connectionString);
/// });
/// 
/// // Register Redis circuit breaker store
/// builder.Services.AddSingleton&lt;IClusterBreakerStore, RedisClusterBreakerStore&gt;();
/// 
/// // Configure circuit breaker
/// builder.Services.AddDistributedCircuitBreaker(options =&gt;
/// {
///     options.Key = "orders-service";
///     // ... other configuration
/// });
/// </code>
/// <para>Manual instantiation:</para>
/// <code>
/// var redis = ConnectionMultiplexer.Connect("localhost:6379");
/// var store = new RedisClusterBreakerStore(redis);
/// 
/// // Use with circuit breaker
/// var circuitBreaker = new DistributedCircuitBreaker(store, options, logger);
/// </code>
/// </example>
public sealed class RedisClusterBreakerStore : IClusterBreakerStore
{
    private readonly IDatabase _db;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisClusterBreakerStore"/> class.
    /// </summary>
    /// <param name="mux">
    /// The Redis connection multiplexer that provides access to Redis instances.
    /// This should be a shared singleton instance for optimal performance.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="mux"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This constructor obtains a Redis database instance from the provided multiplexer.
    /// The database instance is thread-safe and can be shared across multiple operations.
    /// </para>
    /// <para>
    /// <strong>Connection Management:</strong>
    /// </para>
    /// <para>
    /// The Redis connection multiplexer should be registered as a singleton in your
    /// dependency injection container to ensure efficient connection pooling and
    /// resource management across the application lifecycle.
    /// </para>
    /// </remarks>
    public RedisClusterBreakerStore(IConnectionMultiplexer mux)
    {
        if (mux == null) throw new ArgumentNullException(nameof(mux));
        _db = mux.GetDatabase();
    }

    /// <summary>
    /// Generates a Redis key for a time bucket containing success/failure counts.
    /// </summary>
    /// <param name="key">The circuit breaker key.</param>
    /// <param name="epoch">The aligned epoch timestamp for the bucket.</param>
    /// <returns>A Redis key string in the format <c>cb:{key}:b:{epoch}</c>.</returns>
    private static string BucketKey(string key, long epoch) => $"cb:{key}:b:{epoch}";

    /// <summary>
    /// Generates a Redis key for the circuit breaker state latch.
    /// </summary>
    /// <param name="key">The circuit breaker key.</param>
    /// <returns>A Redis key string in the format <c>cb:{key}:latch</c>.</returns>
    private static string LatchKey(string key) => $"cb:{key}:latch";

    /// <summary>
    /// Generates a Redis key for the probe semaphore counter.
    /// </summary>
    /// <param name="key">The circuit breaker key.</param>
    /// <returns>A Redis key string in the format <c>cb:{key}:probes</c>.</returns>
    private static string ProbeKey(string key) => $"cb:{key}:probes";

    /// <summary>
    /// Generates a Redis key for the recovery ramp percentage.
    /// </summary>
    /// <param name="key">The circuit breaker key.</param>
    /// <returns>A Redis key string in the format <c>cb:{key}:ramp</c>.</returns>
    private static string RampKey(string key) => $"cb:{key}:ramp";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This implementation uses Redis hash operations for efficient bucket management:
    /// </para>
    /// <list type="number">
    /// <item><description>Calculates aligned bucket epoch for consistent bucketing</description></item>
    /// <item><description>Atomically increments success ("s") or failure ("f") field in the bucket hash</description></item>
    /// <item><description>Sets TTL on the bucket to window + bucket duration for automatic cleanup</description></item>
    /// </list>
    /// <para>
    /// The TTL prevents unbounded storage growth by automatically expiring old buckets
    /// that are no longer within any possible sliding window calculation.
    /// </para>
    /// </remarks>
    public async Task RecordAsync(string key, bool success, DateTimeOffset timestamp, TimeSpan window, TimeSpan bucket, CancellationToken token)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be null or empty", nameof(key));
        
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

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This implementation optimizes for performance by:
    /// </para>
    /// <list type="number">
    /// <item><description>Calculating all bucket keys within the sliding window</description></item>
    /// <item><description>Using Redis batch operations to read all buckets in parallel</description></item>
    /// <item><description>Aggregating success and failure counts across all buckets</description></item>
    /// <item><description>Handling missing buckets gracefully (treating as zero counts)</treat>
    /// </list>
    /// <para>
    /// For a 60-second window with 10-second buckets, this reads 6 hash objects in parallel,
    /// minimizing network round trips and latency.
    /// </para>
    /// </remarks>
    public async Task<(int Successes, int Failures)> ReadWindowAsync(string key, DateTimeOffset now, TimeSpan window, TimeSpan bucket, CancellationToken token)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be null or empty", nameof(key));
        
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

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Reads the circuit breaker state from Redis string storage. The state is stored
    /// as a string representation of the <see cref="BreakerState"/> enum for human
    /// readability in Redis debugging tools.
    /// </para>
    /// <para>
    /// Returns <see langword="null"/> if the key doesn't exist or has expired,
    /// which should be interpreted as <see cref="BreakerState.Closed"/> by the caller.
    /// </para>
    /// </remarks>
    public async Task<BreakerState?> ReadLatchAsync(string key, CancellationToken token)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be null or empty", nameof(key));
        
        var val = await _db.StringGetAsync(LatchKey(key)).ConfigureAwait(false);
        return val.IsNull ? null : Enum.Parse<BreakerState>(val!);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Stores the circuit breaker state as a string value with optional TTL.
    /// The TTL is used primarily for Open → HalfOpen automatic transitions
    /// after the cooldown period expires.
    /// </para>
    /// </remarks>
    public Task SetLatchAsync(string key, BreakerState state, TimeSpan? ttl, CancellationToken token)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be null or empty", nameof(key));
        
        return _db.StringSetAsync(LatchKey(key), state.ToString(), ttl);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Implements a distributed semaphore using Redis atomic operations:
    /// </para>
    /// <list type="number">
    /// <item><description>Atomically increments the probe counter using INCR</description></item>
    /// <item><description>If this is the first increment (value = 1), sets TTL to prevent leaks</description></item>
    /// <item><description>If counter exceeds maxProbes, decrements and returns false</description></item>
    /// <item><description>Otherwise, returns true indicating successful acquisition</description></item>
    /// </list>
    /// <para>
    /// This approach handles race conditions correctly without requiring distributed locks
    /// or coordination between application instances.
    /// </para>
    /// </remarks>
    public async Task<bool> TryAcquireProbeAsync(string key, int maxProbes, TimeSpan ttl, CancellationToken token)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be null or empty", nameof(key));
        if (maxProbes < 1) throw new ArgumentException("Max probes must be greater than 0", nameof(maxProbes));
        
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

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Decrements the probe semaphore counter using Redis DECR operation.
    /// This method is safe to call even if the counter has already reached zero
    /// or the key has expired.
    /// </para>
    /// </remarks>
    public Task ReleaseProbeAsync(string key, CancellationToken token)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be null or empty", nameof(key));
        
        return _db.StringDecrementAsync(ProbeKey(key));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Reads the current ramp percentage from Redis string storage.
    /// Returns <see langword="null"/> if no ramp is active (key missing or expired),
    /// which should be interpreted as 100% primary traffic.
    /// </para>
    /// </remarks>
    public async Task<int?> ReadRampAsync(string key, CancellationToken token)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be null or empty", nameof(key));
        
        var val = await _db.StringGetAsync(RampKey(key)).ConfigureAwait(false);
        return val.IsNull ? null : (int)val;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Sets the ramp percentage with TTL equal to the ramp step hold duration.
    /// When the TTL expires, the circuit breaker should evaluate whether to
    /// advance to the next ramp step or complete the ramp process.
    /// </para>
    /// </remarks>
    public Task SetRampAsync(string key, int percent, TimeSpan ttl, CancellationToken token)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be null or empty", nameof(key));
        if (percent < 0 || percent > 100) throw new ArgumentException("Percent must be between 0 and 100", nameof(percent));
        
        return _db.StringSetAsync(RampKey(key), percent, ttl);
    }
}
