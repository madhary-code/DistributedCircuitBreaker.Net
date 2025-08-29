using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace DistributedCircuitBreaker.Core;

/// <summary>Default implementation of <see cref="IDistributedCircuitBreaker"/>.</summary>
public sealed class DistributedCircuitBreaker : IDistributedCircuitBreaker
{
    private readonly IClusterBreakerStore _store;
    private readonly CircuitBreakerOptions _options;
    private readonly ILogger<DistributedCircuitBreaker> _logger;
    private readonly ActivitySource _activity = new("DistributedCircuitBreaker");
    private readonly Meter _meter = new("DistributedCircuitBreaker");
    private readonly Counter<long> _requests;
    private readonly Counter<long> _successes;
    private readonly Counter<long> _failures;
    private int _halfOpenSuccessStreak;
    private volatile BreakerState _state;

    public DistributedCircuitBreaker(IClusterBreakerStore store, CircuitBreakerOptions options, ILogger<DistributedCircuitBreaker> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
        _state = BreakerState.Closed;
        _requests = _meter.CreateCounter<long>("requests_total");
        _successes = _meter.CreateCounter<long>("successes_total");
        _failures = _meter.CreateCounter<long>("failures_total");
    }

    /// <inheritdoc />
    public BreakerState State => _state;

    /// <inheritdoc />
    public async Task<EndpointChoice> ChooseAsync(Uri primary, Uri secondary, CancellationToken cancellationToken)
    {
        using var act = _activity.StartActivity("choose");
        _requests.Add(1);
        var latch = await _store.ReadLatchAsync(_options.Key, cancellationToken).ConfigureAwait(false);
        if (latch.HasValue && latch.Value != _state)
        {
            _state = latch.Value;
        }

        switch (_state)
        {
            case BreakerState.Open:
                return new EndpointChoice(secondary, false, 0);
            case BreakerState.HalfOpen:
                if (await _store.TryAcquireProbeAsync(_options.Key, _options.HalfOpenMaxProbes, _options.OpenCooldown, cancellationToken).ConfigureAwait(false))
                {
                    return new EndpointChoice(primary, true, 0);
                }
                return new EndpointChoice(secondary, false, 0);
            default:
                var percent = await _store.ReadRampAsync(_options.Key, cancellationToken).ConfigureAwait(false) ?? 100;
                if (percent < 100)
                {
                    var roll = Random.Shared.Next(0, 100);
                    return roll < percent ? new EndpointChoice(primary, false, percent) : new EndpointChoice(secondary, false, percent);
                }
                return new EndpointChoice(primary, false, 100);
        }
    }

    /// <inheritdoc />
    public async Task ReportAsync(bool success, bool wasProbe, CancellationToken cancellationToken)
    {
        using var act = _activity.StartActivity("report");
        await _store.RecordAsync(_options.Key, success, DateTimeOffset.UtcNow, _options.Window, _options.Bucket, cancellationToken).ConfigureAwait(false);
        if (success)
        {
            _successes.Add(1);
        }
        else
        {
            _failures.Add(1);
        }

        if (_state == BreakerState.Closed)
        {
            await EvaluateOpenAsync(cancellationToken).ConfigureAwait(false);
            await EvaluateRampAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_state == BreakerState.HalfOpen)
        {
            if (wasProbe)
            {
                if (success)
                {
                    if (Interlocked.Increment(ref _halfOpenSuccessStreak) >= _options.HalfOpenSuccessesToClose)
                    {
                        _logger.LogInformation("Breaker {Key} closing", _options.Key);
                        _halfOpenSuccessStreak = 0;
                        _state = BreakerState.Closed;
                        await _store.SetLatchAsync(_options.Key, BreakerState.Closed, null, cancellationToken).ConfigureAwait(false);
                        if (_options.Ramp.Percentages.Length > 0)
                        {
                            await _store.SetRampAsync(_options.Key, _options.Ramp.Percentages[0], _options.Ramp.HoldDuration, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    _halfOpenSuccessStreak = 0;
                    await TripOpenAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        else if (_state == BreakerState.Open)
        {
            // nothing
        }
    }

    private async Task EvaluateOpenAsync(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var (succ, fail) = await _store.ReadWindowAsync(_options.Key, now, _options.Window, _options.Bucket, token).ConfigureAwait(false);
        var total = succ + fail;
        if (total >= _options.MinSamples)
        {
            var rate = fail / (double)total;
            if (rate >= _options.FailureRateToOpen)
            {
                await TripOpenAsync(token).ConfigureAwait(false);
            }
        }
    }

    private async Task EvaluateRampAsync(CancellationToken token)
    {
        var percent = await _store.ReadRampAsync(_options.Key, token).ConfigureAwait(false);
        if (!percent.HasValue || percent.Value >= 100)
        {
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var (succ, fail) = await _store.ReadWindowAsync(_options.Key, now, _options.Window, _options.Bucket, token).ConfigureAwait(false);
        var total = succ + fail;
        var rate = total == 0 ? 0 : fail / (double)total;
        if (rate > _options.Ramp.MaxFailureRatePerStep)
        {
            await TripOpenAsync(token).ConfigureAwait(false);
            return;
        }
        // advance if TTL expired
        var idx = Array.IndexOf(_options.Ramp.Percentages, percent.Value);
        if (idx >= 0 && idx < _options.Ramp.Percentages.Length - 1)
        {
            await _store.SetRampAsync(_options.Key, _options.Ramp.Percentages[idx + 1], _options.Ramp.HoldDuration, token).ConfigureAwait(false);
        }
        else
        {
            await _store.SetRampAsync(_options.Key, 100, _options.Ramp.HoldDuration, token).ConfigureAwait(false);
        }
    }

    private async Task TripOpenAsync(CancellationToken token)
    {
        _logger.LogWarning("Breaker {Key} open", _options.Key);
        _state = BreakerState.Open;
        _halfOpenSuccessStreak = 0;
        await _store.SetLatchAsync(_options.Key, BreakerState.Open, _options.OpenCooldown, token).ConfigureAwait(false);
        await _store.SetRampAsync(_options.Key, 0, _options.Ramp.HoldDuration, token).ConfigureAwait(false);
        _ = Task.Run(async () =>
        {
            await Task.Delay(_options.OpenCooldown, token).ConfigureAwait(false);
            _state = BreakerState.HalfOpen;
            await _store.SetLatchAsync(_options.Key, BreakerState.HalfOpen, _options.OpenCooldown, token).ConfigureAwait(false);
        });
    }
}
