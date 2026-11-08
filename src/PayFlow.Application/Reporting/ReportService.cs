using Microsoft.EntityFrameworkCore;
using PayFlow.Application.Abstractions;

namespace PayFlow.Application.Reporting;

public sealed record ReportRange(DateTimeOffset From, DateTimeOffset To, string TimeZone);

/// <summary>Metrics of one currency. Amounts of different currencies are never added together.</summary>
public sealed record CurrencyOverview(
    string Currency,
    long SuccessfulPayments,
    long FailedPayments,
    decimal? PaymentSuccessRate,
    long GrossPaymentAmountMinor,
    long RefundedAmountMinor,
    long NetCollectedAmountMinor,
    long PaidOrders,
    long AverageOrderValueMinor);

public sealed record StatusCount(string Status, long Count);

public sealed record OverviewReport(
    DateTimeOffset From,
    DateTimeOffset To,
    string TimeZone,
    IReadOnlyList<CurrencyOverview> Currencies,
    long PendingPayments,
    long UnknownPayments,
    long OrdersCreated,
    IReadOnlyList<StatusCount> OrdersByStatus,
    MetricDefinitions Definitions);

public sealed record MetricDefinitions(
    string SuccessfulPayments,
    string FailedPayments,
    string PaymentSuccessRate,
    string GrossPaymentAmount,
    string RefundedAmount,
    string NetCollectedAmount,
    string AverageOrderValue)
{
    public static MetricDefinitions Default { get; } = new(
        "Payments in Succeeded, PartiallyRefunded or Refunded status, by success time.",
        "Payments in Failed status (definitive decline or exhausted retries), by failure time. Pending/Unknown excluded.",
        "Successful / (Successful + Failed). Pending and unknown payments are excluded.",
        "Sum of successful payment amounts before refunds.",
        "Sum of succeeded refunds, by refund completion time.",
        "Gross payment amount - refunded amount, per currency.",
        "Total of paid orders / number of paid orders, by order creation time.");
}

public sealed record RevenuePoint(DateOnly Date, string Currency, long GrossMinor, long RefundedMinor, long NetMinor);

public sealed record RevenueReport(DateTimeOffset From, DateTimeOffset To, string TimeZone, IReadOnlyList<RevenuePoint> Points);

public sealed record FailureCodeCount(string FailureCode, long Count);

public sealed record DailyPaymentStats(DateOnly Date, long Successful, long Failed);

public sealed record PaymentsReport(
    DateTimeOffset From,
    DateTimeOffset To,
    string TimeZone,
    long Successful,
    long Failed,
    decimal? SuccessRate,
    IReadOnlyList<StatusCount> ByStatus,
    IReadOnlyList<FailureCodeCount> FailureCodes,
    IReadOnlyList<DailyPaymentStats> Daily);

public sealed record RefundCurrencyStats(string Currency, long SucceededCount, long SucceededAmountMinor, long PendingCount, long PendingAmountMinor, long FailedCount);

public sealed record RefundsReport(DateTimeOffset From, DateTimeOffset To, string TimeZone, IReadOnlyList<RefundCurrencyStats> ByCurrency);

public sealed record DailyOrderVolume(DateOnly Date, string Currency, long Count, long TotalMinor);

public sealed record OrdersReport(DateTimeOffset From, DateTimeOffset To, string TimeZone, IReadOnlyList<StatusCount> ByStatus, IReadOnlyList<DailyOrderVolume> Daily);

/// <summary>Read-only reporting queries (raw, parameterized SQL in Infrastructure; always filtered by tenant).</summary>
public interface IReportingQueries
{
    Task<OverviewReport> GetOverviewAsync(Guid tenantId, ReportRange range, CancellationToken cancellationToken);

    Task<RevenueReport> GetRevenueAsync(Guid tenantId, ReportRange range, string? currency, CancellationToken cancellationToken);

    Task<PaymentsReport> GetPaymentsAsync(Guid tenantId, ReportRange range, CancellationToken cancellationToken);

    Task<RefundsReport> GetRefundsAsync(Guid tenantId, ReportRange range, CancellationToken cancellationToken);

    Task<OrdersReport> GetOrdersAsync(Guid tenantId, ReportRange range, CancellationToken cancellationToken);
}

public sealed class ReportService(IReportingQueries queries, ITenantsDb tenants, ITenantContext tenantContext, TimeProvider clock)
{
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(366);
    public static readonly TimeSpan DefaultRange = TimeSpan.FromDays(30);

    public async Task<OverviewReport> OverviewAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        var (tenantId, range) = await ResolveAsync(from, to, cancellationToken);
        return await queries.GetOverviewAsync(tenantId, range, cancellationToken);
    }

    public async Task<RevenueReport> RevenueAsync(DateTimeOffset? from, DateTimeOffset? to, string? currency, CancellationToken cancellationToken)
    {
        var (tenantId, range) = await ResolveAsync(from, to, cancellationToken);
        return await queries.GetRevenueAsync(tenantId, range, currency?.Trim().ToUpperInvariant(), cancellationToken);
    }

    public async Task<PaymentsReport> PaymentsAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        var (tenantId, range) = await ResolveAsync(from, to, cancellationToken);
        return await queries.GetPaymentsAsync(tenantId, range, cancellationToken);
    }

    public async Task<RefundsReport> RefundsAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        var (tenantId, range) = await ResolveAsync(from, to, cancellationToken);
        return await queries.GetRefundsAsync(tenantId, range, cancellationToken);
    }

    public async Task<OrdersReport> OrdersAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        var (tenantId, range) = await ResolveAsync(from, to, cancellationToken);
        return await queries.GetOrdersAsync(tenantId, range, cancellationToken);
    }

    /// <summary>Tenant and date range are mandatory for every report; the range is capped to protect the database.</summary>
    public async Task<(Guid TenantId, ReportRange Range)> ResolveAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.RequireTenantId();
        var end = to ?? clock.GetUtcNow();
        var start = from ?? end.Subtract(DefaultRange);
        if (start >= end)
        {
            throw new RequestValidationException("from", "'from' must be earlier than 'to'.");
        }

        if (end - start > MaxRange)
        {
            throw new RequestValidationException("to", $"The report range cannot exceed {MaxRange.TotalDays:0} days.");
        }

        var timeZone = await tenants.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.TimeZone).SingleOrDefaultAsync(cancellationToken)
            ?? "UTC";
        return (tenantId, new ReportRange(start.ToUniversalTime(), end.ToUniversalTime(), timeZone));
    }
}
