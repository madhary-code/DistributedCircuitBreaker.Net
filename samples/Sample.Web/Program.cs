using DistributedCircuitBreaker.Core;
using DistributedCircuitBreaker.Http;
using DistributedCircuitBreaker.Redis;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
var conn = await ConnectionMultiplexer.ConnectAsync(builder.Configuration.GetConnectionString("redis") ?? "localhost:6379");

builder.Services.AddSingleton<IConnectionMultiplexer>(conn);
builder.Services.AddSingleton<IClusterBreakerStore, RedisClusterBreakerStore>();
builder.Services.AddDistributedCircuitBreaker(opts =>
{
    builder.Configuration.GetSection("CircuitBreaker").Bind(opts);
});
builder.Services.AddDualEndpointHttpClient("OrdersClient", new Uri("https://primary"), new Uri("https://secondary"));

builder.Services.AddOpenTelemetry().WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter()).WithMetrics(m => m.AddAspNetCoreInstrumentation().AddOtlpExporter());

var app = builder.Build();
app.MapGet("/call", async (IHttpClientFactory factory) =>
{
    var client = factory.CreateClient("OrdersClient");
    return await client.GetStringAsync("/");
});

app.Run();
