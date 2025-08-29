using DistributedCircuitBreaker.Core;
using Polly;

namespace DistributedCircuitBreaker.Polly;

/// <summary>
/// Polly policy implementation that integrates distributed circuit breaker functionality
/// with the Polly resilience framework for HTTP client resilience patterns.
/// </summary>
/// <remarks>
/// <para>
/// This policy provides seamless integration between the distributed circuit breaker
/// and Polly's rich ecosystem of resilience policies. It can be combined with other
/// Polly policies such as retry, timeout, and bulkhead isolation for comprehensive
/// fault tolerance strategies.
/// </para>
/// <para>
/// <strong>Integration Benefits:</strong>
/// </para>
/// <list type="bullet">
/// <item><description>Combines circuit breaker logic with Polly's policy composition</description></item>
/// <item><description>Supports policy wrapping and chaining with other resilience patterns</description></item>
/// <item><description>Leverages Polly's execution context for request metadata</description></item>
/// <item><description>Compatible with Polly's policy registry and dependency injection extensions</description></item>
/// </list>
/// <para>
/// <strong>Policy Execution Flow:</strong>
/// </para>
/// <list type="number">
/// <item><description>Query distributed circuit breaker for endpoint choice</description></item>
/// <item><description>Set chosen endpoint in Polly context for downstream policies</description></item>
/// <item><description>Execute the wrapped operation (typically HTTP request)</description></item>
/// <item><description>Report success/failure outcome to circuit breaker</description></item>
/// </list>
/// <para>
/// <strong>Context Integration:</strong>
/// </para>
/// <para>
/// The policy sets the <c>"Endpoint"</c> key in the Polly context with the chosen
/// endpoint URI, allowing downstream policies and code to access the selected endpoint.
/// </para>
/// <para>
/// <strong>Failure Handling:</strong>
/// </para>
/// <para>
/// HTTP responses with 2xx status codes are considered successful. All other responses
/// and exceptions are treated as failures for circuit breaker state management.
/// </para>
/// </remarks>
/// <example>
/// <para>Basic usage with HttpClient:</para>
/// <code>
/// var circuitBreaker = serviceProvider.GetRequiredService&lt;IDistributedCircuitBreaker&gt;();
/// 
/// var policy = new DistributedCircuitBreakerPolicy(
///     circuitBreaker,
///     primary: new Uri("https://api.company.com"),
///     secondary: new Uri("https://backup-api.company.com"));
/// 
/// var response = await policy.ExecuteAsync(async (context, ct) =&gt;
/// {
///     var endpoint = (Uri)context["Endpoint"];
///     var httpClient = new HttpClient { BaseAddress = endpoint };
///     return await httpClient.GetAsync("/api/data", ct);
/// }, CancellationToken.None);
/// </code>
/// <para>Policy composition with retry and timeout:</para>
/// <code>
/// var retryPolicy = Policy
///     .Handle&lt;HttpRequestException&gt;()
///     .WaitAndRetryAsync(
///         retryCount: 3,
///         sleepDurationProvider: retryAttempt =&gt; TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
/// 
/// var timeoutPolicy = Policy.TimeoutAsync&lt;HttpResponseMessage&gt;(TimeSpan.FromSeconds(30));
/// 
/// var circuitBreakerPolicy = new DistributedCircuitBreakerPolicy(
///     circuitBreaker, primaryUri, secondaryUri);
/// 
/// // Combine policies: circuit breaker → retry → timeout
/// var combinedPolicy = circuitBreakerPolicy
///     .WrapAsync(retryPolicy)
///     .WrapAsync(timeoutPolicy);
/// 
/// var response = await combinedPolicy.ExecuteAsync(async (context, ct) =&gt;
/// {
///     var endpoint = (Uri)context["Endpoint"];
///     using var httpClient = new HttpClient { BaseAddress = endpoint };
///     return await httpClient.GetAsync("/api/orders", ct);
/// }, CancellationToken.None);
/// </code>
/// <para>Registration with Polly's policy registry:</para>
/// <code>
/// services.AddPolicyRegistry()
///     .Add("CircuitBreaker", new DistributedCircuitBreakerPolicy(
///         serviceProvider.GetRequiredService&lt;IDistributedCircuitBreaker&gt;(),
///         primary: new Uri("https://api.company.com"),
///         secondary: new Uri("https://backup-api.company.com")));
/// 
/// // Use with typed HttpClient
/// services.AddHttpClient&lt;ApiService&gt;()
///     .AddPolicyHandlerFromRegistry("CircuitBreaker");
/// </code>
/// </example>
public sealed class DistributedCircuitBreakerPolicy : AsyncPolicy<HttpResponseMessage>
{
    private readonly IDistributedCircuitBreaker _breaker;
    private readonly Uri _primary;
    private readonly Uri _secondary;

    /// <summary>
    /// Initializes a new instance of the <see cref="DistributedCircuitBreakerPolicy"/> class.
    /// </summary>
    /// <param name="breaker">
    /// The distributed circuit breaker that controls endpoint selection and tracks request outcomes.
    /// </param>
    /// <param name="primary">
    /// The primary endpoint URI to use under normal conditions. This should be the preferred
    /// service endpoint with better performance, features, or geographic proximity.
    /// </param>
    /// <param name="secondary">
    /// The secondary endpoint URI to use during primary endpoint failures. Should serve
    /// a compatible API for seamless failover.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any parameter is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The policy instance is thread-safe and can be reused across multiple operations.
    /// For optimal performance, consider registering it as a singleton in dependency
    /// injection containers or policy registries.
    /// </para>
    /// <para>
    /// <strong>Endpoint Requirements:</strong>
    /// </para>
    /// <list type="bullet">
    /// <item><description>Both URIs must be absolute URIs with scheme and host</description></item>
    /// <item><description>Endpoints should expose compatible API contracts</description></item>
    /// <item><description>Authentication and security configurations should be compatible</description></item>
    /// </list>
    /// </remarks>
    public DistributedCircuitBreakerPolicy(IDistributedCircuitBreaker breaker, Uri primary, Uri secondary)
    {
        _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _secondary = secondary ?? throw new ArgumentNullException(nameof(secondary));
    }

    /// <summary>
    /// Executes the policy implementation with distributed circuit breaker endpoint selection.
    /// </summary>
    /// <param name="action">
    /// The operation to execute. The operation receives a Polly context containing the
    /// selected endpoint in the <c>"Endpoint"</c> key.
    /// </param>
    /// <param name="context">
    /// The Polly execution context. The selected endpoint will be added to this context
    /// with the key <c>"Endpoint"</c>.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the asynchronous operation.</param>
    /// <param name="continueOnCapturedContext">
    /// Whether to continue on the captured context after asynchronous operations.
    /// </param>
    /// <returns>
    /// A task representing the asynchronous policy execution. The task result contains
    /// the HTTP response from the executed operation.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="action"/> or <paramref name="context"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the operation is cancelled via <paramref name="cancellationToken"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This method implements the core policy execution logic:
    /// </para>
    /// <list type="number">
    /// <item><description>Queries the circuit breaker to choose between primary and secondary endpoints</description></item>
    /// <item><description>Sets the chosen endpoint in the Polly context under the <c>"Endpoint"</c> key</description></item>
    /// <item><description>Executes the provided action with the context</description></item>
    /// <item><description>Reports the outcome to the circuit breaker based on HTTP status codes</description></item>
    /// <item><description>Propagates any exceptions while ensuring failure reporting</description></item>
    /// </list>
    /// <para>
    /// <strong>Success/Failure Criteria:</strong>
    /// </para>
    /// <para>
    /// HTTP responses are considered successful if they have status codes in the 200-299 range.
    /// All other status codes and any exceptions are treated as failures for circuit breaker purposes.
    /// </para>
    /// <para>
    /// <strong>Context Usage:</strong>
    /// </para>
    /// <para>
    /// The operation can access the selected endpoint from the context:
    /// </para>
    /// <code>
    /// var endpoint = (Uri)context["Endpoint"];
    /// httpClient.BaseAddress = endpoint;
    /// </code>
    /// </remarks>
    protected override async Task<HttpResponseMessage> ImplementationAsync(Func<Context, CancellationToken, Task<HttpResponseMessage>> action, Context context, CancellationToken cancellationToken, bool continueOnCapturedContext)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        if (context == null) throw new ArgumentNullException(nameof(context));

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
