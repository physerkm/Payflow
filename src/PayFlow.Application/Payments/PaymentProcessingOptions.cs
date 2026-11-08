namespace PayFlow.Application.Payments;

public sealed class PaymentProcessingOptions
{
    public const string SectionName = "Payments";

    /// <summary>Base delay for automatic retries after transient provider errors (exponential backoff).</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Unknown (timed-out) attempts older than this are reconciled via the provider status API.</summary>
    public TimeSpan ReconcileUnknownAfter { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Provider-pending attempts without a webhook after this period are polled.</summary>
    public TimeSpan ReconcileProviderPendingAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Attempts stuck in Processing (e.g. worker crash) after this period are reconciled.</summary>
    public TimeSpan ReconcileProcessingAfter { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>Exponential backoff with "equal jitter", so that many workers do not retry in lockstep.</summary>
public static class RetryBackoff
{
    public static TimeSpan Compute(int attemptInRun, TimeSpan baseDelay, TimeSpan maxDelay, Random? random = null)
    {
        var exponent = Math.Clamp(attemptInRun - 1, 0, 16);
        var exponential = Math.Min(baseDelay.TotalMilliseconds * Math.Pow(2, exponent), maxDelay.TotalMilliseconds);
        var half = exponential / 2;
        var jitter = (random ?? Random.Shared).NextDouble() * half;
        return TimeSpan.FromMilliseconds(half + jitter);
    }
}
