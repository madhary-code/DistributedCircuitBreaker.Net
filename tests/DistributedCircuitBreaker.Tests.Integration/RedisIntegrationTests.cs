using DistributedCircuitBreaker.Core;
using DistributedCircuitBreaker.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.Redis;

namespace DistributedCircuitBreaker.Tests.Integration;

public class RedisIntegrationTests : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder().Build();
    private IConnectionMultiplexer _mux = default!;

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        _mux = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
    }

    public Task DisposeAsync()
    {
        _mux.Dispose();
        return _redis.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task StateSharedBetweenInstances()
    {
        var store = new RedisClusterBreakerStore(_mux);
        var options = new CircuitBreakerOptions("redis-test", TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10), 1, 0.5, TimeSpan.FromSeconds(1), 1, 1, new RampProfile(new[]{100}, TimeSpan.FromSeconds(1),1));
        var breakerA = new DistributedCircuitBreaker(store, options, NullLogger<DistributedCircuitBreaker>.Instance);
        var breakerB = new DistributedCircuitBreaker(store, options, NullLogger<DistributedCircuitBreaker>.Instance);
        var choice = await breakerA.ChooseAsync(new("http://p"), new("http://s"), default);
        await breakerA.ReportAsync(false, choice.UseProbe, default);
        // other instance should read open state
        var choiceB = await breakerB.ChooseAsync(new("http://p"), new("http://s"), default);
        Assert.Equal(new Uri("http://s"), choiceB.Endpoint);
    }
}
