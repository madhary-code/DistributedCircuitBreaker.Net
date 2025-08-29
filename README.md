# DistributedCircuitBreaker.Redis
A distributed circuit breaker for .NET with Redis-backed shared state and built-in primary/secondary endpoint failover.

[![NuGet version](https://img.shields.io/nuget/v/DistributedCircuitBreaker.Redis.svg)](https://www.nuget.org/packages/DistributedCircuitBreaker.Redis)
[![Build](https://github.com/<your-org>/<repo-name>/actions/workflows/build.yml/badge.svg)](https://github.com/<your-org>/<repo-name>/actions)

A **distributed circuit breaker** for .NET applications with **optional primary/secondary endpoint failover**, backed by **Redis** for shared state across multiple nodes and services.

This library helps you prevent cascading failures in microservices, APIs, and backend systems by coordinating breaker state across all your pods/instances.

---

## ✨ Features

- 🚦 Cluster-aware **circuit breaker** (Closed → Open → Half-Open)
- 🌐 **Primary / Secondary endpoint routing** baked into the core
- 📊 **Sliding-window failure tracking** across all nodes using Redis
- 🔁 **Automatic recovery ramp-up** (gradually shift traffic back to primary)
- ⚡ Built for **multi-instance cloud apps** (Kubernetes, App Service, VMs)
- 🔌 Optional **Polly adapter** for integration with existing Polly pipelines
- 🛠️ Simple, extensible API + clean abstractions

---

## 📦 Installation

Install from NuGet:

```bash
dotnet add package DistributedCircuitBreaker.Redis
````

---

## 🚀 Quick Start

```csharp
using DistributedCircuitBreaker.Redis;
using StackExchange.Redis;

// Connect to Redis
var mux = await ConnectionMultiplexer.ConnectAsync("localhost:6379");

// Configure breaker
var breaker = new DistributedCircuitBreaker(
    new RedisClusterBreakerStore(mux),
    new CircuitBreakerOptions(
        key: "orders-service",
        window: TimeSpan.FromSeconds(60),
        bucket: TimeSpan.FromSeconds(10),
        minSamples: 50,
        failureRateToOpen: 0.5,
        openCooldown: TimeSpan.FromSeconds(30),
        halfOpenMaxProbes: 2,
        halfOpenSuccessesToClose: 5,
        ramp: new RampProfile(new[] {10,25,50,75,100}, TimeSpan.FromSeconds(30), 0.2)
    )
);

// Wrap HttpClient with DualEndpointHandler
var httpClient = new HttpClient(
    new DualEndpointHandler(
        breaker,
        new Uri("https://primary.example.com/"),
        new Uri("https://secondary.example.com/")
    )
);

// Use as normal
var response = await httpClient.GetAsync("/health");
Console.WriteLine($"Status: {response.StatusCode}");
```

---

## 🔧 How It Works

1. **Closed** – All traffic goes to the primary endpoint. Failures are tracked in Redis (sliding window).
2. **Open** – When the failure rate crosses the threshold, the breaker trips. All traffic goes to the secondary endpoint (fail-fast primary).
3. **Half-Open** – After the cooldown, a limited number of probes go to the primary.
4. **Recovery Ramp** – On successful probes, traffic gradually shifts back to primary (e.g., 10% → 25% → 50% → 100%).

All breaker state (counters, latch, probes, ramp weight) is shared across **all nodes** via Redis, so the cluster acts as one breaker.

---

## 📊 Architecture

```
+--------------------+          +--------------------+
|   App Instance A   | <------> |                    |
|   Circuit Breaker  |          |                    |
|   (local cache)    |          |       Redis        |
+--------------------+          |   (shared state)   |
                                +--------------------+
+--------------------+          
|   App Instance B   |          
|   Circuit Breaker  |          
|   (local cache)    |          
+--------------------+          

   Primary Endpoint <----> Secondary Endpoint
```

---

## 🛣️ Roadmap

* [ ] ASP.NET Core `HttpClientFactory` integration
* [ ] gRPC client middleware
* [ ] Observability hooks (OpenTelemetry, Prometheus)
* [ ] Admin CLI for breaker inspection/reset
* [ ] Pluggable stores (etcd, SQL) beyond Redis

---

## 🤝 Contributing

Contributions are welcome! 🎉

1. Fork the repo
2. Create a feature branch (`git checkout -b feature/my-feature`)
3. Commit your changes (`git commit -m "Add my feature"`)
4. Push and open a Pull Request

Please make sure to update documentation and tests where relevant. See [CONTRIBUTING.md](CONTRIBUTING.md) for more details.

---

## 📜 License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.

---

## 🙌 Acknowledgements

* [Polly](https://github.com/App-vNext/Polly) for inspiring the resilience patterns
* [StackExchange.Redis](https://github.com/StackExchange/StackExchange.Redis) for the Redis client

```

---

✅ This is **ready to drop into your repo**.  
Bro, do you also want me to prep a **`CONTRIBUTING.md` + GitHub Actions build workflow** so your repo looks professional from day one?
```
