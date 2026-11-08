using Microsoft.Extensions.Logging;
using PayFlow.Application.Abstractions;
using PayFlow.Infrastructure.Observability;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace PayFlow.Infrastructure.PaymentProviders;

public sealed class ProviderResilienceOptions
{
    public const string SectionName = "Payments:Resilience";

    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public double FailureRatio { get; set; } = 0.5;

    public int MinimumThroughput { get; set; } = 8;

    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Decorates the real provider adapter with a per-call timeout and a circuit breaker. A timeout of a charge is
/// reported as <see cref="ChargeOutcome.Timeout"/> (unknown outcome), never as a failure; an open circuit is
/// reported as <see cref="ChargeOutcome.CircuitOpen"/> so the provider is not called at all.
/// </summary>
internal sealed class ResilientPaymentProvider : IPaymentProvider
{
    private readonly IPaymentProvider _inner;
    private readonly ILogger<ResilientPaymentProvider> _logger;
    private readonly ResiliencePipeline _pipeline;

    public ResilientPaymentProvider(IPaymentProvider inner, ProviderResilienceOptions options, ILogger<ResilientPaymentProvider> logger)
    {
        _inner = inner;
        _logger = logger;
        _pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = options.FailureRatio,
                MinimumThroughput = options.MinimumThroughput,
                SamplingDuration = options.SamplingDuration,
                BreakDuration = options.BreakDuration,
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is not null and not OperationCanceledException
                    || args.Outcome.Result is PaymentResult { Outcome: ChargeOutcome.TransientError or ChargeOutcome.Timeout }
                    || args.Outcome.Result is RefundResult { Outcome: RefundOutcome.TransientError }),
                OnOpened = _ =>
                {
                    logger.LogWarning("Payment provider circuit opened");
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    logger.LogInformation("Payment provider circuit closed");
                    return ValueTask.CompletedTask;
                },
            })
            .AddTimeout(options.CallTimeout)
            .Build();
    }

    public string Name => _inner.Name;

    public async Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken cancellationToken)
    {
        using var activity = ProviderActivity.Start("charge", Name);
        PaymentResult result;
        try
        {
            result = await _pipeline.ExecuteAsync(async ct => await _inner.ChargeAsync(request, ct), cancellationToken);
        }
        catch (TimeoutRejectedException)
        {
            result = new PaymentResult(ChargeOutcome.Timeout);
        }
        catch (BrokenCircuitException)
        {
            result = new PaymentResult(ChargeOutcome.CircuitOpen, FailureCode: "provider_circuit_open");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Provider charge failed with a transport error");
            result = new PaymentResult(ChargeOutcome.TransientError, FailureCode: "provider_transport_error");
        }

        activity?.SetTag("payflow.provider.outcome", result.Outcome.ToString());
        PayFlowMetrics.PaymentOutcomes.Add(1, new KeyValuePair<string, object?>("outcome", result.Outcome.ToString()));
        return result;
    }

    public async Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken)
    {
        using var activity = ProviderActivity.Start("refund", Name);
        try
        {
            var result = await _pipeline.ExecuteAsync(async ct => await _inner.RefundAsync(request, ct), cancellationToken);
            activity?.SetTag("payflow.provider.outcome", result.Outcome.ToString());
            return result;
        }
        catch (Exception ex) when (ex is TimeoutRejectedException or BrokenCircuitException or HttpRequestException)
        {
            // Safe to retry: the refund call carries an idempotency key.
            return new RefundResult(RefundOutcome.TransientError, FailureCode: "provider_unavailable");
        }
    }

    public async Task<PaymentStatusResult> GetPaymentStatusAsync(string providerReference, CancellationToken cancellationToken)
    {
        using var activity = ProviderActivity.Start("status", Name);
        return await _pipeline.ExecuteAsync(async ct => await _inner.GetPaymentStatusAsync(providerReference, ct), cancellationToken);
    }
}
