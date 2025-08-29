using DistributedCircuitBreaker.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace DistributedCircuitBreaker.Tests.Unit;

public class BreakerTests
{
    private static CircuitBreakerOptions Options => new(
        key: "test",
        window: TimeSpan.FromSeconds(60),
        bucket: TimeSpan.FromSeconds(10),
        minSamples: 1,
        failureRateToOpen: 0.5,
        openCooldown: TimeSpan.FromSeconds(1),
        halfOpenMaxProbes: 1,
        halfOpenSuccessesToClose: 1,
        ramp: new RampProfile(new[] { 100 }, TimeSpan.FromSeconds(1), 1));

    [Fact]
    public async Task OpensOnFailures()
    {
        var store = new InMemoryClusterBreakerStore();
        var breaker = new DistributedCircuitBreaker(store, Options, NullLogger<DistributedCircuitBreaker>.Instance);
        var choice = await breaker.ChooseAsync(new("http://p"), new("http://s"), default);
        await breaker.ReportAsync(false, choice.UseProbe, default);
        Assert.Equal(BreakerState.Open, breaker.State);
    }
}
