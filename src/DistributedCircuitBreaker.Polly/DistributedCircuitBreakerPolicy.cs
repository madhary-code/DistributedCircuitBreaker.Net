using DistributedCircuitBreaker.Core;
using Polly;

namespace DistributedCircuitBreaker.Polly;

/// <summary>Polly policy delegating to <see cref="IDistributedCircuitBreaker"/>.</summary>
public sealed class DistributedCircuitBreakerPolicy : AsyncPolicy<HttpResponseMessage>
{
    private readonly IDistributedCircuitBreaker _breaker;
    private readonly Uri _primary;
    private readonly Uri _secondary;

    public DistributedCircuitBreakerPolicy(IDistributedCircuitBreaker breaker, Uri primary, Uri secondary)
    {
        _breaker = breaker;
        _primary = primary;
        _secondary = secondary;
    }

    protected override async Task<HttpResponseMessage> ImplementationAsync(Func<Context, CancellationToken, Task<HttpResponseMessage>> action, Context context, CancellationToken cancellationToken, bool continueOnCapturedContext)
    {
        var choice = await _breaker.ChooseAsync(_primary, _secondary, cancellationToken).ConfigureAwait(false);
        context["Endpoint"] = choice.Endpoint;
        try
        {
            var result = await action(context, cancellationToken).ConfigureAwait(false);
            await _breaker.ReportAsync(result.IsSuccessStatusCode, choice.UseProbe, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await _breaker.ReportAsync(false, choice.UseProbe, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
