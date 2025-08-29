using StackExchange.Redis;

namespace DistributedQuotaCircuitBreaker.Redis;

/// <summary>
/// Redis-backed quota circuit breaker for selecting between primary and secondary endpoints.
/// </summary>
/// <remarks>
/// The circuit breaker increments a Redis counter for each request routed to the primary endpoint.
/// When the configured quota is exceeded within the quota period, subsequent requests are routed to
/// the secondary endpoint until the Redis key expires. The key's time-to-live acts as the quota reset
/// interval, ensuring that traffic returns to the primary endpoint once the period elapses.
/// </remarks>
public sealed class RedisQuotaCircuitBreaker
{
    private readonly IDatabase _db;
    private readonly string _countKey;
    private readonly int _quota;
    private readonly TimeSpan _period;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisQuotaCircuitBreaker"/> class.
    /// </summary>
    /// <param name="mux">Redis connection multiplexer.</param>
    /// <param name="key">Unique key used to track quota usage in Redis.</param>
    /// <param name="quota">Number of requests allowed within the period for the primary endpoint.</param>
    /// <param name="period">Quota reset interval.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="mux"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="quota"/> is not positive or <paramref name="period"/> is non-positive.</exception>
    public RedisQuotaCircuitBreaker(IConnectionMultiplexer mux, string key, int quota, TimeSpan period)
    {
        if (mux == null) throw new ArgumentNullException(nameof(mux));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key cannot be null or empty", nameof(key));
        if (quota <= 0) throw new ArgumentOutOfRangeException(nameof(quota));
        if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period));

        _db = mux.GetDatabase();
        _countKey = $"qb:{key}";
        _quota = quota;
        _period = period;
    }

    /// <summary>
    /// Chooses the appropriate endpoint based on current quota usage.
    /// </summary>
    /// <param name="primary">Primary endpoint URI.</param>
    /// <param name="secondary">Secondary endpoint URI.</param>
    /// <param name="token">Cancellation token to observe.</param>
    /// <returns>The selected endpoint URI.</returns>
    /// <remarks>
    /// This method atomically increments the quota counter in Redis. If the quota is exceeded,
    /// the secondary endpoint is returned. When the key expires, the quota resets and the primary
    /// endpoint will be chosen again.
    /// </remarks>
    public async Task<Uri> ChooseEndpointAsync(Uri primary, Uri secondary, CancellationToken token = default)
    {
        if (primary == null) throw new ArgumentNullException(nameof(primary));
        if (secondary == null) throw new ArgumentNullException(nameof(secondary));

        var count = await _db.StringIncrementAsync(_countKey).ConfigureAwait(false);
        if (count == 1)
        {
            await _db.KeyExpireAsync(_countKey, _period).ConfigureAwait(false);
        }

        return count <= _quota ? primary : secondary;
    }
}
