using System.Globalization;
using System.Text.Json;
using PayFlow.Application.Reporting;

namespace PayFlow.Application.Assistant;

/// <summary>
/// The only things the assistant can do: four read-only, tenant-scoped report queries (spec §15.2). The model never
/// writes SQL, never chooses the tenant and cannot change data. Inputs are validated here regardless of what the
/// model sends, and every result states its metric definitions and date range so answers can cite them.
/// </summary>
public sealed class AssistantTools(IReportingQueries reports)
{
    public const int MaxRangeDays = 92;
    public const int MaxRows = 10;

    // Tool results are read by the model, not rendered as HTML: keep quotes and non-ASCII characters readable.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string[] DateParameters = ["from", "to"];

    public static IReadOnlyList<AssistantToolSpec> Specs { get; } =
    [
        Spec("get_revenue_report", "Gross collected, refunded and net revenue per currency for a date range, with the top days.", withCurrency: true),
        Spec("get_payment_statistics", "Payment counts by status and the payment success rate (successful / (successful + failed), pending excluded) for a date range."),
        Spec("get_failed_payment_summary", "Failed payments grouped by failure reason (e.g. card_declined, provider_unavailable) for a date range."),
        Spec("get_refund_summary", "Refund counts and amounts per currency (succeeded, in progress, failed) for a date range."),
    ];

    public async Task<AssistantToolResult> ExecuteAsync(AssistantToolCall call, Guid tenantId, string timeZone, CancellationToken cancellationToken)
    {
        if (Specs.All(s => s.Name != call.Name))
        {
            return Error($"Tool '{call.Name}' is not available. Allowed tools: {string.Join(", ", Specs.Select(s => s.Name))}.");
        }

        if (!TryParseInput(call, timeZone, out var range, out var currency, out var error))
        {
            return Error(error!);
        }

        object result = call.Name switch
        {
            "get_revenue_report" => await RevenueAsync(tenantId, range!, currency, cancellationToken),
            "get_payment_statistics" => await PaymentStatsAsync(tenantId, range!, cancellationToken),
            "get_failed_payment_summary" => await FailedAsync(tenantId, range!, cancellationToken),
            _ => await RefundsAsync(tenantId, range!, cancellationToken),
        };

        return new AssistantToolResult(JsonSerializer.Serialize(result, Json), IsError: false);
    }

    private async Task<object> RevenueAsync(Guid tenantId, ReportRange range, string? currency, CancellationToken ct)
    {
        var report = await reports.GetRevenueAsync(tenantId, range, currency, ct);
        var perCurrency = report.Points
            .GroupBy(p => p.Currency)
            .Select(g => new
            {
                currency = g.Key,
                grossCollected = Major(g.Sum(p => p.GrossMinor), g.Key),
                refunded = Major(g.Sum(p => p.RefundedMinor), g.Key),
                net = Major(g.Sum(p => p.NetMinor), g.Key),
                topDays = g.Where(p => p.GrossMinor > 0).OrderByDescending(p => p.GrossMinor).Take(5)
                    .Select(p => new { date = p.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), gross = Major(p.GrossMinor, g.Key) }),
            })
            .Take(MaxRows)
            .ToList();
        return Envelope("revenue", range, perCurrency, MetricDefinitions.Default.GrossPaymentAmount + " " + MetricDefinitions.Default.NetCollectedAmount);
    }

    private async Task<object> PaymentStatsAsync(Guid tenantId, ReportRange range, CancellationToken ct)
    {
        var report = await reports.GetPaymentsAsync(tenantId, range, ct);
        return Envelope(
            "payment_statistics",
            range,
            new { report.Successful, report.Failed, report.SuccessRate, byStatus = report.ByStatus.Take(MaxRows) },
            MetricDefinitions.Default.PaymentSuccessRate);
    }

    private async Task<object> FailedAsync(Guid tenantId, ReportRange range, CancellationToken ct)
    {
        var report = await reports.GetPaymentsAsync(tenantId, range, ct);
        return Envelope("failed_payments", range, new { totalFailed = report.Failed, byReason = report.FailureCodes.Take(MaxRows) }, MetricDefinitions.Default.FailedPayments);
    }

    private async Task<object> RefundsAsync(Guid tenantId, ReportRange range, CancellationToken ct)
    {
        var report = await reports.GetRefundsAsync(tenantId, range, ct);
        var rows = report.ByCurrency.Take(MaxRows).Select(r => new
        {
            r.Currency,
            succeededCount = r.SucceededCount,
            succeededAmount = Major(r.SucceededAmountMinor, r.Currency),
            inProgressCount = r.PendingCount,
            inProgressAmount = Major(r.PendingAmountMinor, r.Currency),
            failedCount = r.FailedCount,
        });
        return Envelope("refunds", range, rows, MetricDefinitions.Default.RefundedAmount);
    }

    private static object Envelope(string metric, ReportRange range, object data, string definition) => new
    {
        metric,
        definition,
        period = new { from = range.From, to = range.To, timeZone = range.TimeZone },
        note = "Amounts are decimal strings in major units, grouped per currency and never converted.",
        data,
    };

    private static string Major(long minor, string currency) =>
        Notifications.NotificationHandler.FormatAmount(minor, currency).Split(' ')[0];

    /// <summary>Validates the model-supplied input. Unknown properties (e.g. a smuggled tenantId) are rejected.</summary>
    internal static bool TryParseInput(AssistantToolCall call, string timeZone, out ReportRange? range, out string? currency, out string? error)
    {
        range = null;
        currency = null;
        error = null;
        if (call.Input.ValueKind != JsonValueKind.Object)
        {
            error = "Tool input must be a JSON object.";
            return false;
        }

        var allowed = call.Name == "get_revenue_report" ? [.. DateParameters, "currency"] : DateParameters;
        foreach (var property in call.Input.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                error = $"Unknown parameter '{property.Name}'. Tools are always scoped to the signed-in company; allowed parameters: {string.Join(", ", allowed)}.";
                return false;
            }
        }

        if (!TryDate(call.Input, "from", out var from) || !TryDate(call.Input, "to", out var to))
        {
            error = "Parameters 'from' and 'to' are required dates in YYYY-MM-DD format.";
            return false;
        }

        if (to < from)
        {
            error = "'to' must not be earlier than 'from'.";
            return false;
        }

        if (to.DayNumber - from.DayNumber + 1 > MaxRangeDays)
        {
            error = $"The date range is limited to {MaxRangeDays} days.";
            return false;
        }

        if (call.Input.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String)
        {
            currency = c.GetString()!.Trim().ToUpperInvariant();
            if (!Domain.Common.CurrencyCatalog.IsSupported(currency))
            {
                error = $"Unsupported currency '{currency}'.";
                return false;
            }
        }

        // Dates are interpreted in the tenant's time zone: [from 00:00, to+1 00:00).
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        range = new ReportRange(StartOfDay(from, zone), StartOfDay(to.AddDays(1), zone), timeZone);
        return true;
    }

    private static DateTimeOffset StartOfDay(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static bool TryDate(JsonElement input, string name, out DateOnly value)
    {
        value = default;
        return input.TryGetProperty(name, out var element)
               && element.ValueKind == JsonValueKind.String
               && DateOnly.TryParseExact(element.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    private static AssistantToolResult Error(string message) => new(JsonSerializer.Serialize(new { error = message }, Json), IsError: true);

    private static AssistantToolSpec Spec(string name, string description, bool withCurrency = false)
    {
        var properties = new Dictionary<string, JsonElement>
        {
            ["from"] = JsonSerializer.SerializeToElement(new { type = "string", description = "First day (inclusive), YYYY-MM-DD, in the company's time zone." }),
            ["to"] = JsonSerializer.SerializeToElement(new { type = "string", description = $"Last day (inclusive), YYYY-MM-DD. At most {MaxRangeDays} days after 'from'." }),
        };
        if (withCurrency)
        {
            properties["currency"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Optional ISO 4217 code (e.g. TRY, USD) to limit the report to one currency." });
        }

        return new AssistantToolSpec(name, description, properties, DateParameters);
    }
}
