using DistributedCircuitBreaker.Core;
using Microsoft.Extensions.Logging;

namespace DistributedCircuitBreaker.Http;

/// <summary>DelegatingHandler that routes using <see cref="IDistributedCircuitBreaker"/>.</summary>
public sealed class DualEndpointHandler : DelegatingHandler
{
    private readonly IDistributedCircuitBreaker _breaker;
    private readonly Uri _primary;
    private readonly Uri _secondary;
    private readonly ILogger<DualEndpointHandler> _logger;

    public DualEndpointHandler(IDistributedCircuitBreaker breaker, Uri primary, Uri secondary, ILogger<DualEndpointHandler> logger)
    {
        _breaker = breaker;
        _primary = primary;
        _secondary = secondary;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var choice = await _breaker.ChooseAsync(_primary, _secondary, cancellationToken).ConfigureAwait(false);
        var original = request.RequestUri!;
        request.RequestUri = new Uri(choice.Endpoint, request.RequestUri.PathAndQuery);
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await _breaker.ReportAsync(response.IsSuccessStatusCode, choice.UseProbe, cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch
        {
            await _breaker.ReportAsync(false, choice.UseProbe, cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            request.RequestUri = original;
        }
    }
}
