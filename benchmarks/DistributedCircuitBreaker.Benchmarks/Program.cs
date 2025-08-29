using BenchmarkDotNet.Running;
using DistributedCircuitBreaker.Benchmarks;

BenchmarkRunner.Run<BreakerBenchmarks>();
