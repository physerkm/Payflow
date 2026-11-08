using System.Net;
using System.Text;
using System.Text.Json;
using Npgsql;
using PayFlow.Infrastructure.PaymentProviders;
using PayFlow.IntegrationTests.Infrastructure;

namespace PayFlow.IntegrationTests;

/// <summary>
/// Runs the application exactly like docker-compose does: business queries use the non-owner role
/// <c>payflow_app</c> (PostgreSQL RLS enforced, app.tenant_id set per command), while migrations and the
/// cross-tenant system lookups use the owner role.
/// </summary>
public sealed class RlsApiFixture : ApiFixture
{
    public const string AppRole = "payflow_app";
    public const string AppPassword = "payflow_app_test_pw";

    protected override async Task<IReadOnlyDictionary<string, string?>?> PrepareAsync(string connectionString)
    {
        // The role is cluster-wide; it must exist before migrations so they can grant it table privileges.
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{AppRole}') THEN
                        CREATE ROLE {AppRole} LOGIN PASSWORD '{AppPassword}' NOSUPERUSER NOBYPASSRLS;
                    END IF;
                END $$;
                """,
                connection);
            await command.ExecuteNonQueryAsync();
        }

        var appConnection = new NpgsqlConnectionStringBuilder(connectionString) { Username = AppRole, Password = AppPassword }.ConnectionString;
        return new Dictionary<string, string?>
        {
            ["ConnectionStrings:PayFlow"] = appConnection,
            ["ConnectionStrings:PayFlowSystem"] = connectionString,
            ["ConnectionStrings:PayFlowMigrations"] = connectionString,
            ["Database:EnableRowLevelSecurity"] = "true",
        };
    }
}

public sealed class RowLevelSecurityModeTests(RlsApiFixture fixture) : IntegrationTest<RlsApiFixture>(fixture)
{
    [Fact]
    public async Task Full_payment_refund_and_reporting_flow_works_under_rls()
    {
        var owner = await NewTenantOwnerAsync();
        var order = await owner.CreateSimpleOrderAsync(priceMinor: 10_000);
        var payment = await owner.PayOkAsync(order);
        await WaitUntilAsync(async () => await owner.PaymentStatusAsync(payment) == "Succeeded", because: "worker must process under the app role");
        (await owner.OrderStatusAsync(order)).ShouldBe("Paid");

        (await owner.PostWithKeyAsync($"/api/v1/payments/{payment}/refunds", new { amountMinor = 2_500 }, "rls-refund")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await WaitUntilAsync(async () => await owner.PaymentStatusAsync(payment) == "PartiallyRefunded");

        var overview = await owner.GetJsonAsync("/api/v1/reports/overview");
        overview.GetProperty("currencies")[0].GetProperty("netCollectedAmountMinor").GetInt64().ShouldBe(7_500);
        await WaitUntilAsync(async () => (await owner.GetJsonAsync("/api/v1/notifications")).GetArrayLength() >= 2);
    }

    [Fact]
    public async Task Tenants_stay_isolated_and_idempotency_replays_under_rls()
    {
        var a = await NewTenantOwnerAsync();
        var b = await NewTenantOwnerAsync();
        var order = await a.CreateSimpleOrderAsync();

        var first = await a.PayAsync(order, "pm_test_success", "rls-key");
        var replay = await a.PayAsync(order, "pm_test_success", "rls-key");
        replay.Headers.GetValues("Idempotency-Replayed").Single().ShouldBe("true");
        var paymentId = JsonDocument.Parse(await first.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("paymentId").GetGuid();

        (await b.Http.GetAsync($"/api/v1/payments/{paymentId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.GetJsonAsync("/api/v1/orders")).GetProperty("totalCount").GetInt64().ShouldBe(0);
    }

    [Fact]
    public async Task Webhooks_and_reconciliation_resolve_payments_under_rls()
    {
        var owner = await NewTenantOwnerAsync();
        var timeoutOrder = await owner.CreateSimpleOrderAsync();
        var timeoutPayment = await owner.PayOkAsync(timeoutOrder, "pm_test_timeout");
        await WaitUntilAsync(async () => await owner.PaymentStatusAsync(timeoutPayment) == "Succeeded", because: "reconciliation via the system data source");

        var pendingOrder = await owner.CreateSimpleOrderAsync();
        var pendingPayment = await owner.PayOkAsync(pendingOrder, "pm_test_pending");
        string? reference = null;
        await WaitUntilAsync(async () =>
        {
            reference = (await owner.GetJsonAsync($"/api/v1/payments/{pendingPayment}/attempts"))[0].GetProperty("providerReference").GetString();
            return reference is not null;
        });

        var body = JsonSerializer.Serialize(new { id = "evt_rls_" + Guid.NewGuid().ToString("N"), type = "charge.succeeded", data = new { providerReference = reference, amountMinor = 10_000, currency = "TRY" } });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments/mock") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add(WebhookSignature.HeaderName, WebhookSignature.Create(PayFlowApiFactory.WebhookSecret, DateTimeOffset.UtcNow, body));
        var response = await Factory.CreateClient().SendAsync(request, Ct);

        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Processed");
        (await owner.PaymentStatusAsync(pendingPayment)).ShouldBe("Succeeded");
    }
}
