using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PayFlow.Contracts;
using PayFlow.Contracts.Events;
using PayFlow.Infrastructure.Messaging;
using PayFlow.Infrastructure.Persistence;
using PayFlow.IntegrationTests.Infrastructure;

namespace PayFlow.IntegrationTests;

public sealed class ReliabilityTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Broker_outage_keeps_the_message_in_the_outbox_and_it_is_published_after_recovery()
    {
        var owner = await NewTenantOwnerAsync();
        var order = await owner.CreateSimpleOrderAsync();

        Factory.Bus.SimulateOutage = true;
        try
        {
            var payment = await owner.PayOkAsync(order); // the API still accepts the payment (202)

            await WaitUntilAsync(async () => await Factory.WithDbAsync(db =>
                db.OutboxMessages.AnyAsync(m => m.EventType == PaymentRequestedV1.EventType && m.Status == OutboxStatus.Pending && m.PublishAttempts > 0 && m.TenantId == owner.TenantId)));
            (await owner.PaymentStatusAsync(payment)).ShouldBe("Pending");

            Factory.Bus.SimulateOutage = false;
            await WaitUntilAsync(async () => await owner.PaymentStatusAsync(payment) == "Succeeded", TimeSpan.FromSeconds(30), "outbox should publish after recovery");
            (await owner.OrderStatusAsync(order)).ShouldBe("Paid");
        }
        finally
        {
            Factory.Bus.SimulateOutage = false;
        }
    }

    [Fact]
    public async Task Duplicate_delivery_of_the_same_message_has_a_single_business_effect()
    {
        var owner = await NewTenantOwnerAsync();
        var order = await owner.CreateSimpleOrderAsync();
        var payment = await owner.PayOkAsync(order);
        await WaitUntilAsync(async () => (await owner.GetJsonAsync("/api/v1/notifications")).GetArrayLength() == 1);

        var envelope = MessageEnvelope.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, owner.TenantId, "dup-test", new PaymentSucceededV1(payment, order, 10_000, "TRY"));
        var dispatcher = Factory.Services.GetRequiredService<IMessageDispatcher>();

        await dispatcher.DispatchAsync(envelope, null, Ct);
        await dispatcher.DispatchAsync(envelope, null, Ct); // at-least-once delivery: same messageId again

        (await owner.GetJsonAsync("/api/v1/notifications")).GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Redelivered_payment_request_does_not_charge_twice()
    {
        var owner = await NewTenantOwnerAsync();
        var order = await owner.CreateSimpleOrderAsync();
        var payment = await owner.PayOkAsync(order);
        await WaitUntilAsync(async () => await owner.PaymentStatusAsync(payment) == "Succeeded");

        var attempt = (await owner.GetJsonAsync($"/api/v1/payments/{payment}/attempts"))[0].GetProperty("id").GetGuid();
        var redelivery = MessageEnvelope.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, owner.TenantId, "redelivery", new PaymentRequestedV1(payment, attempt));
        await Factory.Services.GetRequiredService<IMessageDispatcher>().DispatchAsync(redelivery, null, Ct);

        var charges = await Factory.WithDbAsync(db => db.MockProviderTransactions.CountAsync(t => t.IdempotencyKey == attempt.ToString("N")));
        charges.ShouldBe(1);
        (await owner.GetJsonAsync($"/api/v1/payments/{payment}/attempts")).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Message_with_a_foreign_tenant_id_cannot_touch_the_payment()
    {
        var a = await NewTenantOwnerAsync();
        var b = await NewTenantOwnerAsync();
        var order = await a.CreateSimpleOrderAsync();
        var payment = await a.PayOkAsync(order);
        await WaitUntilAsync(async () => await a.PaymentStatusAsync(payment) == "Succeeded");

        // A forged message claims tenant B for tenant A's payment: the tenant-scoped lookup finds nothing.
        var forged = MessageEnvelope.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, b.TenantId, "forged", new RefundRequestedV1(Guid.NewGuid(), payment));
        await Should.ThrowAsync<NonRetryableMessageException>(() =>
            Factory.Services.GetRequiredService<IMessageDispatcher>().DispatchAsync(forged, null, Ct));
    }
}

public sealed class DatabaseConstraintTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Composite_foreign_key_blocks_an_order_pointing_to_another_tenants_customer()
    {
        var a = await NewTenantOwnerAsync();
        var b = await NewTenantOwnerAsync();
        var customerA = await a.CreateCustomerAsync();

        await using var connection = new NpgsqlConnection(Factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO orders (id, tenant_id, order_number, customer_id, status, total_minor, currency, created_at, updated_at, row_version)
            VALUES (gen_random_uuid(), @tenant, 'HACK-1', @customer, 'Draft', 100, 'TRY', now(), now(), 1)
            """,
            connection);
        command.Parameters.AddWithValue("tenant", b.TenantId!.Value);
        command.Parameters.AddWithValue("customer", customerA);

        var ex = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
        ex.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        ex.ConstraintName.ShouldBe("fk_orders_customer_tenant");
    }

    [Fact]
    public async Task Check_constraint_prevents_refunds_beyond_the_payment_amount()
    {
        var owner = await NewTenantOwnerAsync();
        var order = await owner.CreateSimpleOrderAsync();
        var payment = await owner.PayOkAsync(order);

        await using var connection = new NpgsqlConnection(Factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("UPDATE payments SET refunded_amount_minor = amount_minor + 1 WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", payment);

        var ex = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
        ex.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Row_level_security_isolates_tenants_for_the_application_role()
    {
        var a = await NewTenantOwnerAsync();
        var b = await NewTenantOwnerAsync();
        await a.CreateCustomerAsync("a1");
        await a.CreateCustomerAsync("a2");
        await b.CreateCustomerAsync("b1");

        var role = "rls_" + Guid.NewGuid().ToString("N")[..10];
        var database = new NpgsqlConnectionStringBuilder(Factory.ConnectionString).Database;
        await ExecuteAsync(Factory.ConnectionString, $"CREATE ROLE {role} LOGIN PASSWORD 'rls-test-pw'; GRANT SELECT, INSERT ON customers TO {role};");
        try
        {
            var appConnection = new NpgsqlConnectionStringBuilder(Factory.ConnectionString) { Username = role, Password = "rls-test-pw", Pooling = false }.ConnectionString;
            await using var connection = new NpgsqlConnection(appConnection);
            await connection.OpenAsync(Ct);

            (await ScalarAsync(connection, "SELECT COUNT(*) FROM customers")).ShouldBe(0L, "no tenant context → no rows");

            await ScalarAsync(connection, $"SELECT set_config('app.tenant_id', '{a.TenantId}', false)");
            (await ScalarAsync(connection, "SELECT COUNT(*) FROM customers")).ShouldBe(2L);

            await ScalarAsync(connection, $"SELECT set_config('app.tenant_id', '{b.TenantId}', false)");
            (await ScalarAsync(connection, "SELECT COUNT(*) FROM customers")).ShouldBe(1L);

            // WITH CHECK: cannot write rows for another tenant while acting as tenant B.
            var insert = $"INSERT INTO customers (id, tenant_id, name, status, created_at, updated_at, row_version) VALUES (gen_random_uuid(), '{a.TenantId}', 'x', 'Active', now(), now(), 1)";
            var ex = await Should.ThrowAsync<PostgresException>(() => ScalarAsync(connection, insert));
            ex.SqlState.ShouldBe("42501"); // insufficient_privilege: new row violates row-level security policy
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(Factory.ConnectionString, $"DROP OWNED BY {role}; DROP ROLE {role};");
        }

        database.ShouldNotBeNullOrEmpty();
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
