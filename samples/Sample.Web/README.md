# Circuit Breaker Chaos Sample

This sample demonstrates the `DistributedCircuitBreaker` library with a failing primary endpoint and automatic failover to a secondary endpoint.

## Architecture

The project hosts three minimal APIs in the same process:

- **Primary API** (`http://localhost:5010`) – randomly fails to simulate an unstable service.
- **Secondary API** (`http://localhost:5020`) – always succeeds and acts as the fallback.
- **Demo API** (`http://localhost:5000`) – exposes endpoints to invoke the client and inspect the breaker state.

All HTTP calls from the demo API use `DualEndpointHttpClient`, which consults the circuit breaker before each request. When failures exceed the configured threshold, traffic automatically switches from the primary to the secondary API.

## Prerequisites

- .NET 8 SDK
- A Redis instance available at `localhost:6379`

## Running the sample

```bash
dotnet run --project samples/Sample.Web
```

In another terminal, issue repeated requests:

```bash
# Send a request that is routed through the circuit breaker
curl http://localhost:5000/call

# Inspect the current breaker state (Closed, Open, HalfOpen)
curl http://localhost:5000/breaker
```

Observe the `endpoint` field in the `/call` response. After enough failures, it switches from `5010` (primary) to `5020` (secondary), demonstrating automatic failover. The `/breaker` endpoint reports the breaker state so you can visualize when the circuit opens and closes.
