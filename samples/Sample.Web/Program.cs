using DistributedCircuitBreaker.Core;
using DistributedCircuitBreaker.Http;
using DistributedCircuitBreaker.Redis;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using StackExchange.Redis;

var primaryApp = BuildPrimaryApi();
var secondaryApp = BuildSecondaryApi();
var app = await BuildDemoApiAsync(args);

await Task.WhenAll(
    primaryApp.RunAsync(),
    secondaryApp.RunAsync(),
    app.RunAsync());

static WebApplication BuildPrimaryApi()
{
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://localhost:5010");
    var app = builder.Build();
    app.MapGet("/", () =>
    {
        // 70% failure rate to trigger circuit breaker
        if (Random.Shared.NextDouble() < 0.7)
        {
            return Results.Problem("Primary failure", statusCode: 500);
        }
        return Results.Ok("Primary response");
    });
    return app;
}

static WebApplication BuildSecondaryApi()
{
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://localhost:5020");
    var app = builder.Build();
    app.MapGet("/", () => Results.Ok("Secondary response"));
    return app;
}

static async Task<WebApplication> BuildDemoApiAsync(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);
    var conn = await ConnectionMultiplexer.ConnectAsync(builder.Configuration.GetConnectionString("redis") ?? "localhost:6379");

    builder.Services.AddSingleton<IConnectionMultiplexer>(conn);
    builder.Services.AddSingleton<IClusterBreakerStore, RedisClusterBreakerStore>();
    builder.Services.AddDistributedCircuitBreaker(opts =>
    {
        builder.Configuration.GetSection("CircuitBreaker").Bind(opts);
    });
    builder.Services.AddDualEndpointHttpClient("ChaosClient", new("http://localhost:5010"), new("http://localhost:5020"));

    builder.Services.AddOpenTelemetry()
        .WithTracing(t => t.AddAspNetCoreInstrumentation())
        .WithMetrics(m => m.AddAspNetCoreInstrumentation());

    var app = builder.Build();
    app.MapGet("/call", async (IHttpClientFactory factory) =>
    {
        var client = factory.CreateClient("ChaosClient");
        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();
        return Results.Json(new
        {
            endpoint = response.RequestMessage?.RequestUri?.ToString(),
            status = (int)response.StatusCode,
            body
        });
    });

    app.MapGet("/breaker", (IDistributedCircuitBreaker breaker) => breaker.State.ToString());

    return app;
}
