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
        var store = new Tests.Unit.InMemoryClusterBreakerStore();
        var options = new CircuitBreakerOptions("bench", TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10), 10, 0.5, TimeSpan.FromSeconds(5), 1, 1, new RampProfile(new[]{100}, TimeSpan.FromSeconds(1),1));
        _breaker = new DistributedCircuitBreaker(store, options, NullLogger<DistributedCircuitBreaker>.Instance);
    }

    [Benchmark]
    public async Task ChooseReport()
    {
        var choice = await _breaker.ChooseAsync(new("http://p"), new("http://s"), default);
        await _breaker.ReportAsync(true, choice.UseProbe, default);
    }
}
