# DistributedCircuitBreaker.Redis

> **Note**: this repository originally contained only this README. The full .NET solution is generated at first clone; run `dotnet build` to restore and compile all projects.

A production-grade distributed circuit breaker for .NET 8 with Redis-backed shared state, primary/secondary failover, recovery ramp-up and OpenTelemetry.

## Projects
- `DistributedCircuitBreaker.Core` – core state machine and abstractions
- `DistributedCircuitBreaker.Redis` – Redis-backed store
- `DistributedCircuitBreaker.Http` – Dual endpoint `HttpClient` handler
- `DistributedCircuitBreaker.Polly` – Polly adapter
- `DistributedCircuitBreaker.Tests.Unit` / `Integration`
- `DistributedCircuitBreaker.Benchmarks`
- `samples/Sample.Web` – chaos testing sample with primary/secondary failover

## Getting Started
```bash
dotnet build
dotnet test
```

Configure in ASP.NET Core:
```csharp
builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect("localhost"));
builder.Services.AddSingleton<IClusterBreakerStore, RedisClusterBreakerStore>();
builder.Services.AddDistributedCircuitBreaker(options =>
{
    options.Key = "orders";
    options.Window = TimeSpan.FromSeconds(60);
    options.Bucket = TimeSpan.FromSeconds(10);
    options.MinSamples = 50;
    options.FailureRateToOpen = 0.5;
    options.OpenCooldown = TimeSpan.FromSeconds(30);
    options.HalfOpenMaxProbes = 2;
    options.HalfOpenSuccessesToClose = 5;
    options.Ramp = new RampProfile(new[]{10,25,50,75,100}, TimeSpan.FromSeconds(30), 0.2);
});
```

Add an HTTP client:
```csharp
builder.Services.AddDualEndpointHttpClient("Orders", new("https://primary"), new("https://secondary"));
```

## Telemetry
The library emits OpenTelemetry traces and metrics using ActivitySource `DistributedCircuitBreaker` and Meter `DistributedCircuitBreaker`.

## Versioning
Packages are versioned with [MinVer](https://github.com/adamralph/minver) from git tags `v*.*.*`.

## Contributing
PRs welcome. Please run `dotnet test` before submitting.

## License
MIT
