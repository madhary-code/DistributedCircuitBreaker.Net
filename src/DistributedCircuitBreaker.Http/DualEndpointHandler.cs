using DistributedCircuitBreaker.Core;
using Microsoft.Extensions.Logging;

namespace DistributedCircuitBreaker.Http;

/// <summary>
/// HTTP message handler that provides automatic primary/secondary endpoint routing 
/// using a distributed circuit breaker for resilient HTTP client communication.
/// </summary>
/// <remarks>
/// <para>
/// This handler integrates seamlessly with <see cref="HttpClient"/> and the .NET HTTP client factory
/// to provide transparent failover capabilities. When the circuit breaker is open due to failures
/// on the primary endpoint, all requests are automatically routed to the secondary endpoint.
/// </para>
/// <para>
/// <strong>Key Features:</strong>
/// </para>
/// <list type="bullet">
/// <item><description>Transparent HTTP endpoint failover based on circuit breaker state</description></item>
/// <item><description>Automatic success/failure reporting to the circuit breaker</description></item>
/// <item><description>Preserves original request URI for proper HTTP client behavior</description></item>
/// <item><description>Supports all HTTP methods and maintains request/response semantics</description></item>
/// <item><description>Integrates with ASP.NET Core dependency injection and HTTP client factory</description></item>
/// </list>
/// <para>
/// <strong>Circuit Breaker Integration:</strong>
/// </para>
/// <para>
/// The handler automatically calls <see cref="IDistributedCircuitBreaker.ChooseAsync"/> before each
/// request to determine the target endpoint, and <see cref="IDistributedCircuitBreaker.ReportAsync"/>
/// after each request to update failure statistics.
/// </para>
/// <para>
/// <strong>Success/Failure Determination:</strong>
/// </para>
/// <para>
/// HTTP responses with status codes in the 200-299 range are considered successful.
/// All other status codes and exceptions (network timeouts, connection failures, etc.)
/// are treated as failures for circuit breaker purposes.
/// </para>
/// <para>
/// <strong>Request URI Handling:</strong>
/// </para>
/// <para>
/// The handler preserves the original request URI structure by combining the chosen
/// endpoint base URI with the request's path and query components. This ensures
/// proper routing while maintaining compatibility with existing HTTP client code.
/// </para>
/// </remarks>
/// <example>
/// <para>Basic setup with HTTP client factory:</para>
/// <code>
/// // In Program.cs or Startup.cs
/// services.AddDualEndpointHttpClient("OrdersApi", 
///     primary: new Uri("https://orders-api.company.com"),
///     secondary: new Uri("https://orders-api-backup.company.com"));
/// 
/// // In a service class
/// public class OrdersService
/// {
///     private readonly HttpClient _httpClient;
///     
///     public OrdersService(IHttpClientFactory httpClientFactory)
///     {
///         _httpClient = httpClientFactory.CreateClient("OrdersApi");
///     }
///     
///     public async Task&lt;Order&gt; GetOrderAsync(int orderId)
///     {
///         // This request will automatically use primary or secondary endpoint
///         // based on circuit breaker state
///         var response = await _httpClient.GetAsync($"/api/orders/{orderId}");
///         response.EnsureSuccessStatusCode();
///         return await response.Content.ReadFromJsonAsync&lt;Order&gt;();
///     }
/// }
/// </code>
/// <para>Manual setup with HttpClient:</para>
/// <code>
/// var circuitBreaker = serviceProvider.GetRequiredService&lt;IDistributedCircuitBreaker&gt;();
/// var logger = serviceProvider.GetRequiredService&lt;ILogger&lt;DualEndpointHandler&gt;&gt;();
/// 
/// var handler = new DualEndpointHandler(
///     circuitBreaker,
///     primary: new Uri("https://api.example.com"),
///     secondary: new Uri("https://backup-api.example.com"),
///     logger);
/// 
/// var httpClient = new HttpClient(handler);
/// </code>
/// </example>
public sealed class DualEndpointHandler : DelegatingHandler
{
    private readonly IDistributedCircuitBreaker _breaker;
    private readonly Uri _primary;
    private readonly Uri _secondary;
    private readonly ILogger<DualEndpointHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DualEndpointHandler"/> class.
    /// </summary>
    /// <param name="breaker">
    /// The distributed circuit breaker that controls endpoint selection and tracks request outcomes.
    /// </param>
    /// <param name="primary">
    /// The primary endpoint URI to use under normal conditions. This should be the preferred
    /// service endpoint with better performance or features.
    /// </param>
    /// <param name="secondary">
    /// The secondary endpoint URI to use during failures or circuit breaker open state.
    /// This should be a reliable fallback that can handle the same requests as the primary.
    /// </param>
    /// <param name="logger">
    /// Logger instance for operational visibility and debugging HTTP routing decisions.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any parameter is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The primary and secondary endpoints should expose compatible APIs to ensure seamless
    /// failover. Path and query components from the original request will be preserved
    /// when routing to either endpoint.
    /// </para>
    /// <para>
    /// <strong>Endpoint URI Requirements:</strong>
    /// </para>
    /// <list type="bullet">
    /// <item><description>Must be absolute URIs with scheme and host</description></item>
    /// <item><description>Should not include path components (will be overridden by request path)</description></item>
    /// <item><description>Both endpoints should serve compatible API contracts</description></item>
    /// </list>
    /// </remarks>
    public DualEndpointHandler(IDistributedCircuitBreaker breaker, Uri primary, Uri secondary, ILogger<DualEndpointHandler> logger)
    {
        _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _secondary = secondary ?? throw new ArgumentNullException(nameof(secondary));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Sends an HTTP request using circuit breaker-controlled endpoint selection.
    /// </summary>
    /// <param name="request">The HTTP request message to send.</param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <returns>
    /// A task representing the asynchronous operation. The task result contains
    /// the HTTP response message from the chosen endpoint.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="HttpRequestException">
    /// Thrown when the HTTP request fails due to network issues, timeout, or server errors.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the operation is cancelled via <paramref name="cancellationToken"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This method implements the core circuit breaker integration logic:
    /// </para>
    /// <list type="number">
    /// <item><description>Queries the circuit breaker to choose between primary and secondary endpoints</description></item>
    /// <item><description>Modifies the request URI to target the chosen endpoint</description></item>
    /// <item><description>Sends the request through the HTTP client pipeline</description></item>
    /// <item><description>Reports the outcome (success/failure) to the circuit breaker</description></item>
    /// <item><description>Restores the original request URI for proper cleanup</description></item>
    /// </list>
    /// <para>
    /// <strong>URI Routing Logic:</strong>
    /// </para>
    /// <para>
    /// The original request URI's path and query components are preserved and combined
    /// with the chosen endpoint's base URI. For example:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Original request: <c>/api/orders/123?expand=items</c></description></item>
    /// <item><description>Primary endpoint: <c>https://orders-api.company.com</c></description></item>
    /// <item><description>Final URI: <c>https://orders-api.company.com/api/orders/123?expand=items</c></description></item>
    /// </list>
    /// <para>
    /// <strong>Error Handling:</strong>
    /// </para>
    /// <para>
    /// All exceptions during request processing are reported as failures to the circuit breaker
    /// before being rethrown. This ensures accurate failure tracking for state transitions.
    /// </para>
    /// </remarks>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (request.RequestUri == null) throw new ArgumentException("Request URI cannot be null", nameof(request));

        var choice = await _breaker.ChooseAsync(_primary, _secondary, cancellationToken).ConfigureAwait(false);
        var original = request.RequestUri;
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
