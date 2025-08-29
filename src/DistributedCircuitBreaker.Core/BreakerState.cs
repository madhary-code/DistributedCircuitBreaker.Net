namespace DistributedCircuitBreaker.Core;

/// <summary>Represents the cluster state.</summary>
public enum BreakerState
{
    Closed = 0,
    Open = 1,
    HalfOpen = 2,
}
