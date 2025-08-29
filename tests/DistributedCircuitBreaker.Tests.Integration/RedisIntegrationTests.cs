using DistributedCircuitBreaker.Core;
using DistributedCircuitBreaker.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;
using CoreBreaker = DistributedCircuitBreaker.Core.DistributedCircuitBreaker;

namespace DistributedCircuitBreaker.Tests.Integration;

/// <summary>
/// Integration tests for the Redis-backed cluster breaker store.
/// </summary>
public class RedisIntegrationTests : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder().Build();
    private IConnectionMultiplexer _mux = default!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        _mux = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _mux.Dispose();
        return _redis.DisposeAsync().AsTask();
    }

    /// <summary>
    /// Confirms that multiple circuit breaker instances share state via Redis.
    /// </summary>
    [Fact]
    public async Task StateSharedBetweenInstances()
    {
        var store = new RedisClusterBreakerStore(_mux);
        var options = new CircuitBreakerOptions("redis-test", TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10), 1, 0.5, TimeSpan.FromSeconds(1), 1, 1, new RampProfile(new[]{100}, TimeSpan.FromSeconds(1),1));
        var breakerA = new CoreBreaker(store, options, NullLogger<CoreBreaker>.Instance);
        var breakerB = new CoreBreaker(store, options, NullLogger<CoreBreaker>.Instance);
        var choice = await breakerA.ChooseAsync(new("http://p"), new("http://s"), default);
        await breakerA.ReportAsync(false, choice.UseProbe, default);
        // other instance should read open state
        var choiceB = await breakerB.ChooseAsync(new("http://p"), new("http://s"), default);
        Assert.Equal(new Uri("http://s"), choiceB.Endpoint);
    }
}
