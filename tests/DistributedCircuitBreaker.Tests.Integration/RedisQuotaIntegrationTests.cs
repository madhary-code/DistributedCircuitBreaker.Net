using DistributedQuotaCircuitBreaker.Redis;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace DistributedCircuitBreaker.Tests.Integration;

/// <summary>
/// Integration tests for the <see cref="RedisQuotaCircuitBreaker"/> using a real Redis container.
/// </summary>
public class RedisQuotaIntegrationTests : IAsyncLifetime
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
    /// Ensures traffic is diverted to the secondary endpoint after exceeding the quota
    /// and returns to the primary endpoint once the quota period elapses.
    /// </summary>
    [Fact]
    public async Task RoutesToSecondaryWhenQuotaExceededAndResets()
    {
        var breaker = new RedisQuotaCircuitBreaker(_mux, "quota-test", quota: 2, period: TimeSpan.FromMilliseconds(200));
        var primary = new Uri("http://p");
        var secondary = new Uri("http://s");

        Assert.Equal(primary, await breaker.ChooseEndpointAsync(primary, secondary));
        Assert.Equal(primary, await breaker.ChooseEndpointAsync(primary, secondary));
        Assert.Equal(secondary, await breaker.ChooseEndpointAsync(primary, secondary));

        await Task.Delay(250);

        Assert.Equal(primary, await breaker.ChooseEndpointAsync(primary, secondary));
    }
}
