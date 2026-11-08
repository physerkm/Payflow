using Npgsql;
using PayFlow.Application.Reporting;

namespace PayFlow.Infrastructure.Reporting;

/// <summary>
/// Read-only reporting SQL. Every statement filters by <c>tenant_id</c> explicitly (raw SQL bypasses EF query
/// filters) and runs in a read-only transaction that also sets <c>app.tenant_id</c> for RLS.
/// Amounts are grouped per currency; different currencies are never summed.
/// </summary>
internal sealed class ReportingQueries(NpgsqlDataSource dataSource) : IReportingQueries
{
    private const string SuccessfulStatuses = "('Succeeded', 'PartiallyRefunded', 'Refunded')";

    public async Task<OverviewReport> GetOverviewAsync(Guid tenantId, ReportRange range, CancellationToken cancellationToken)
    {
        return await RunAsync(tenantId, async (connection, transaction) =>
        {
            var currencies = new List<CurrencyOverview>();
            await using (var command = Command(connection, transaction, tenantId, range, $"""
                WITH succ AS (
                    SELECT currency, COUNT(*) AS cnt, COALESCE(SUM(amount_minor), 0) AS gross
                    FROM payments
                    WHERE tenant_id = @tenant AND status IN {SuccessfulStatuses} AND succeeded_at >= @from AND succeeded_at < @to
                    GROUP BY currency),
                fail AS (
                    SELECT currency, COUNT(*) AS cnt FROM payments
                    WHERE tenant_id = @tenant AND status = 'Failed' AND failed_at >= @from AND failed_at < @to
                    GROUP BY currency),
                ref AS (
                    SELECT currency, COALESCE(SUM(amount_minor), 0) AS amount FROM refunds
                    WHERE tenant_id = @tenant AND status = 'Succeeded' AND completed_at >= @from AND completed_at < @to
                    GROUP BY currency),
                ord AS (
                    SELECT currency, COUNT(*) AS cnt, COALESCE(SUM(total_minor), 0) AS total FROM orders
                    WHERE tenant_id = @tenant AND status = 'Paid' AND created_at >= @from AND created_at < @to
                    GROUP BY currency),
                cur AS (SELECT currency FROM succ UNION SELECT currency FROM fail UNION SELECT currency FROM ref UNION SELECT currency FROM ord)
                SELECT cur.currency,
                       COALESCE(succ.cnt, 0), COALESCE(fail.cnt, 0), COALESCE(succ.gross, 0),
                       COALESCE(ref.amount, 0), COALESCE(ord.cnt, 0), COALESCE(ord.total, 0)
                FROM cur
                LEFT JOIN succ ON succ.currency = cur.currency
                LEFT JOIN fail ON fail.currency = cur.currency
                LEFT JOIN ref ON ref.currency = cur.currency
                LEFT JOIN ord ON ord.currency = cur.currency
                ORDER BY cur.currency
                """))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var successful = reader.GetInt64(1);
                    var failed = reader.GetInt64(2);
                    var gross = reader.GetInt64(3);
                    var refunded = reader.GetInt64(4);
                    var paidOrders = reader.GetInt64(5);
                    var paidTotal = reader.GetInt64(6);
                    currencies.Add(new CurrencyOverview(
                        reader.GetString(0).Trim(),
                        successful,
                        failed,
                        successful + failed == 0 ? null : Math.Round((decimal)successful / (successful + failed), 4),
                        gross,
                        refunded,
                        gross - refunded,
                        paidOrders,
                        paidOrders == 0 ? 0 : (long)Math.Round(paidTotal / (decimal)paidOrders, MidpointRounding.AwayFromZero)));
                }
            }

            long pending = 0, unknown = 0;
            await using (var command = Command(connection, transaction, tenantId, range,
                "SELECT COUNT(*) FILTER (WHERE status = 'Pending'), COUNT(*) FILTER (WHERE status = 'Unknown') FROM payments WHERE tenant_id = @tenant"))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    pending = reader.GetInt64(0);
                    unknown = reader.GetInt64(1);
                }
            }

            var byStatus = await StatusCountsAsync(connection, transaction, tenantId, range,
                "SELECT status, COUNT(*) FROM orders WHERE tenant_id = @tenant AND created_at >= @from AND created_at < @to GROUP BY status ORDER BY status",
                cancellationToken);

            return new OverviewReport(range.From, range.To, range.TimeZone, currencies, pending, unknown, byStatus.Sum(s => s.Count), byStatus, MetricDefinitions.Default);
        }, cancellationToken);
    }

    public async Task<RevenueReport> GetRevenueAsync(Guid tenantId, ReportRange range, string? currency, CancellationToken cancellationToken)
    {
        return await RunAsync(tenantId, async (connection, transaction) =>
        {
            var rows = new Dictionary<(DateOnly, string), (long Gross, long Refunded)>();
            await using (var command = Command(connection, transaction, tenantId, range, $"""
                SELECT day, currency, SUM(gross)::bigint, SUM(refunded)::bigint FROM (
                    SELECT (succeeded_at AT TIME ZONE @tz)::date AS day, currency, SUM(amount_minor) AS gross, 0 AS refunded
                    FROM payments
                    WHERE tenant_id = @tenant AND status IN {SuccessfulStatuses} AND succeeded_at >= @from AND succeeded_at < @to
                      AND (@currency::text IS NULL OR currency = @currency)
                    GROUP BY 1, 2
                    UNION ALL
                    SELECT (completed_at AT TIME ZONE @tz)::date, currency, 0, SUM(amount_minor)
                    FROM refunds
                    WHERE tenant_id = @tenant AND status = 'Succeeded' AND completed_at >= @from AND completed_at < @to
                      AND (@currency::text IS NULL OR currency = @currency)
                    GROUP BY 1, 2
                ) x
                GROUP BY day, currency
                ORDER BY day, currency
                """))
            {
                command.Parameters.Add(new NpgsqlParameter("currency", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)currency ?? DBNull.Value });
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows[(reader.GetFieldValue<DateOnly>(0), reader.GetString(1).Trim())] = (reader.GetInt64(2), reader.GetInt64(3));
                }
            }

            // Fill gaps with zero so charts get a continuous daily series per currency.
            var points = new List<RevenuePoint>();
            var currencies = rows.Keys.Select(k => k.Item2).Distinct().Order(StringComparer.Ordinal).ToList();
            foreach (var day in Days(range))
            {
                foreach (var cur in currencies)
                {
                    var (gross, refunded) = rows.GetValueOrDefault((day, cur));
                    points.Add(new RevenuePoint(day, cur, gross, refunded, gross - refunded));
                }
            }

            return new RevenueReport(range.From, range.To, range.TimeZone, points);
        }, cancellationToken);
    }

    public async Task<PaymentsReport> GetPaymentsAsync(Guid tenantId, ReportRange range, CancellationToken cancellationToken)
    {
        return await RunAsync(tenantId, async (connection, transaction) =>
        {
            var byStatus = await StatusCountsAsync(connection, transaction, tenantId, range,
                "SELECT status, COUNT(*) FROM payments WHERE tenant_id = @tenant AND created_at >= @from AND created_at < @to GROUP BY status ORDER BY status",
                cancellationToken);

            var failureCodes = new List<FailureCodeCount>();
            await using (var command = Command(connection, transaction, tenantId, range, """
                SELECT COALESCE(failure_code, 'unknown'), COUNT(*) FROM payments
                WHERE tenant_id = @tenant AND status = 'Failed' AND failed_at >= @from AND failed_at < @to
                GROUP BY 1 ORDER BY 2 DESC
                """))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    failureCodes.Add(new FailureCodeCount(reader.GetString(0), reader.GetInt64(1)));
                }
            }

            var daily = new Dictionary<DateOnly, (long Ok, long Failed)>();
            await using (var command = Command(connection, transaction, tenantId, range, $"""
                SELECT day, SUM(ok)::bigint, SUM(failed)::bigint FROM (
                    SELECT (succeeded_at AT TIME ZONE @tz)::date AS day, COUNT(*) AS ok, 0 AS failed FROM payments
                    WHERE tenant_id = @tenant AND status IN {SuccessfulStatuses} AND succeeded_at >= @from AND succeeded_at < @to GROUP BY 1
                    UNION ALL
                    SELECT (failed_at AT TIME ZONE @tz)::date, 0, COUNT(*) FROM payments
                    WHERE tenant_id = @tenant AND status = 'Failed' AND failed_at >= @from AND failed_at < @to GROUP BY 1
                ) x GROUP BY day
                """))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    daily[reader.GetFieldValue<DateOnly>(0)] = (reader.GetInt64(1), reader.GetInt64(2));
                }
            }

            var successful = daily.Values.Sum(v => v.Ok);
            var failed = daily.Values.Sum(v => v.Failed);
            return new PaymentsReport(
                range.From,
                range.To,
                range.TimeZone,
                successful,
                failed,
                successful + failed == 0 ? null : Math.Round((decimal)successful / (successful + failed), 4),
                byStatus,
                failureCodes,
                Days(range).Select(d => new DailyPaymentStats(d, daily.GetValueOrDefault(d).Ok, daily.GetValueOrDefault(d).Failed)).ToList());
        }, cancellationToken);
    }

    public async Task<RefundsReport> GetRefundsAsync(Guid tenantId, ReportRange range, CancellationToken cancellationToken)
    {
        return await RunAsync(tenantId, async (connection, transaction) =>
        {
            var stats = new List<RefundCurrencyStats>();
            await using var command = Command(connection, transaction, tenantId, range, """
                SELECT currency,
                    COUNT(*) FILTER (WHERE status = 'Succeeded' AND completed_at >= @from AND completed_at < @to),
                    COALESCE(SUM(amount_minor) FILTER (WHERE status = 'Succeeded' AND completed_at >= @from AND completed_at < @to), 0),
                    COUNT(*) FILTER (WHERE status = 'Pending'),
                    COALESCE(SUM(amount_minor) FILTER (WHERE status = 'Pending'), 0),
                    COUNT(*) FILTER (WHERE status = 'Failed' AND completed_at >= @from AND completed_at < @to)
                FROM refunds
                WHERE tenant_id = @tenant AND (created_at >= @from OR status = 'Pending') AND created_at < @to
                GROUP BY currency ORDER BY currency
                """);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                stats.Add(new RefundCurrencyStats(reader.GetString(0).Trim(), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5)));
            }

            return new RefundsReport(range.From, range.To, range.TimeZone, stats);
        }, cancellationToken);
    }

    public async Task<OrdersReport> GetOrdersAsync(Guid tenantId, ReportRange range, CancellationToken cancellationToken)
    {
        return await RunAsync(tenantId, async (connection, transaction) =>
        {
            var byStatus = await StatusCountsAsync(connection, transaction, tenantId, range,
                "SELECT status, COUNT(*) FROM orders WHERE tenant_id = @tenant AND created_at >= @from AND created_at < @to GROUP BY status ORDER BY status",
                cancellationToken);

            var rows = new Dictionary<(DateOnly, string), (long Count, long Total)>();
            await using var command = Command(connection, transaction, tenantId, range, """
                SELECT (created_at AT TIME ZONE @tz)::date AS day, currency, COUNT(*), COALESCE(SUM(total_minor), 0)::bigint
                FROM orders
                WHERE tenant_id = @tenant AND created_at >= @from AND created_at < @to AND status <> 'Cancelled'
                GROUP BY 1, 2 ORDER BY 1, 2
                """);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows[(reader.GetFieldValue<DateOnly>(0), reader.GetString(1).Trim())] = (reader.GetInt64(2), reader.GetInt64(3));
            }

            // Continuous daily series per currency (zero days included) for charts.
            var currencies = rows.Keys.Select(k => k.Item2).Distinct().Order(StringComparer.Ordinal).ToList();
            var daily = Days(range)
                .SelectMany(day => currencies.Select(cur =>
                {
                    var (count, total) = rows.GetValueOrDefault((day, cur));
                    return new DailyOrderVolume(day, cur, count, total);
                }))
                .ToList();

            return new OrdersReport(range.From, range.To, range.TimeZone, byStatus, daily);
        }, cancellationToken);
    }

    private static IEnumerable<DateOnly> Days(ReportRange range)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(range.TimeZone);
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(range.From, zone).Date);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(range.To.AddTicks(-1), zone).Date);
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            yield return day;
        }
    }

    private static async Task<IReadOnlyList<StatusCount>> StatusCountsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenantId, ReportRange range, string sql, CancellationToken cancellationToken)
    {
        var result = new List<StatusCount>();
        await using var command = Command(connection, transaction, tenantId, range, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new StatusCount(reader.GetString(0), reader.GetInt64(1)));
        }

        return result;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenantId, ReportRange range, string sql)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("from", range.From);
        command.Parameters.AddWithValue("to", range.To);
        command.Parameters.AddWithValue("tz", range.TimeZone);
        return command;
    }

    private async Task<T> RunAsync<T>(Guid tenantId, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        await using (var setup = new NpgsqlCommand("SET TRANSACTION READ ONLY; SELECT set_config('app.tenant_id', @tenant, true);", connection, transaction))
        {
            setup.Parameters.AddWithValue("tenant", tenantId.ToString());
            await setup.ExecuteNonQueryAsync(cancellationToken);
        }

        var result = await work(connection, transaction);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
