using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayFlow.Application.Abstractions;

namespace PayFlow.Infrastructure.Persistence;

/// <summary>
/// Defense layer 3 (optional): PostgreSQL Row-Level Security. Before EF executes a command, the current tenant
/// is written to the session setting <c>app.tenant_id</c>, which the RLS policies compare with each row.
/// <para>
/// The applied value is tracked per physical connection and re-applied whenever the connection is (re)opened,
/// a transaction rolls back (rolled-back SETs are undone by PostgreSQL) or the tenant context changes.
/// Npgsql resets session state when a connection returns to the pool, so a previous request's tenant can
/// never leak into the next one.
/// </para>
/// </summary>
internal sealed class TenantSessionInterceptor(ITenantContext tenantContext) : DbCommandInterceptor, IDbConnectionInterceptor, IDbTransactionInterceptor
{
    private bool _known;
    private Guid? _applied;

    public void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => _known = false;

    public Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        _known = false;
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => _known = false;

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        _known = false;
        return Task.CompletedTask;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Apply(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(command, cancellationToken);
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Apply(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(command, cancellationToken);
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Apply(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(command, cancellationToken);
        return result;
    }

    private bool NeedsApply(out string value)
    {
        var tenant = tenantContext.TenantId;
        value = tenant?.ToString() ?? string.Empty;
        return !_known || _applied != tenant;
    }

    private void Apply(DbCommand command)
    {
        if (!NeedsApply(out var value))
        {
            return;
        }

        using var set = CreateSetCommand(command, value);
        set.ExecuteNonQuery();
        Remember();
    }

    private async Task ApplyAsync(DbCommand command, CancellationToken cancellationToken)
    {
        if (!NeedsApply(out var value))
        {
            return;
        }

        await using var set = CreateSetCommand(command, value);
        await set.ExecuteNonQueryAsync(cancellationToken);
        Remember();
    }

    private void Remember()
    {
        _applied = tenantContext.TenantId;
        _known = true;
    }

    private static DbCommand CreateSetCommand(DbCommand command, string value)
    {
        var set = command.Connection!.CreateCommand();
        set.Transaction = command.Transaction;
        set.CommandText = "SELECT set_config('app.tenant_id', @payflow_tenant, false)";
        var parameter = set.CreateParameter();
        parameter.ParameterName = "payflow_tenant";
        parameter.Value = value;
        set.Parameters.Add(parameter);
        return set;
    }
}
