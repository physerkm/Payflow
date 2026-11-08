using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.PaymentProviders;
using PayFlow.IntegrationTests.Infrastructure;

namespace PayFlow.IntegrationTests;

public sealed class PaymentFlowTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Successful_payment_is_accepted_asynchronously_then_marks_the_order_paid()
    {
        var owner = await NewTenantOwnerAsync();
        var order = await owner.CreateSimpleOrderAsync(priceMinor: 150075);

        var response = await owner.PayAsync(order, "pm_test_success");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        accepted.GetProperty("amountMinor").GetInt64().ShouldBe(150075); // amount comes from the server
        accepted.GetProperty("status").GetString().ShouldBe("Pending");
        response.Headers.Location!.ToString().ShouldBe(accepted.GetProperty("statusUrl").GetString());

        var paymentId = accepted.GetProperty("paymentId").GetGuid();
        await WaitUntilAsync(async () => await owner.PaymentStatusAsync(paymentId) == "Succeeded");
        (await owner.OrderStatusAsync(order)).ShouldBe("Paid");

        // The notification consumer received PaymentSucceeded.v1 through the outbox.
        await WaitUntilAsync(async () => (await owner.GetJsonAsync("/api/v1/notifications")).GetArrayLength() == 1);
    }

    [Fact]
    public async Task Declined_payment_fails_the_order_and_is_not_retried()
    {
        var owner = await NewTenantOwnerAsync();
        var order = await owner.CreateSimpleOrderAsync();
        var payment = await owner.PayOkAsync(order, "pm_test_decline");

        await WaitUntilAsync(async () => await owner.PaymentStatusAsync(payment) == "Failed");
        (await owner.OrderStatusAsync(order)).ShouldBe("PaymentFailed");
        var attempts = await owner.GetJsonAsync($"/api/v1/payments/{payment}/attempts");
        attempts.GetArrayLength().ShouldBe(1);
        attempts[0].GetProperty("failureCode").GetString().ShouldBe("card_declined");
    }

    [Fact]
    public async Task Timeout_becomes_unknown_and_is_reconciled_without_a_second_charge()
    {
        var owner = await NewTenantOwnerAsync();
        var order = await owner.CreateSimpleOrderAsync();
        var payment = await owner.PayOkAsync(order, "pm_test_timeout");

        await WaitUntilAsync(async () => await owner.PaymentStatusAsync(payment) == "Succeeded", because: "reconciliation should confirm the charge");
        (await owner.OrderStatusAsync(order)).ShouldBe("Paid");
        (await owner.GetJsonAsync($"/api/v1/payments/{payment}/attempts")).GetArrayLength().ShouldBe(1);

        // Exactly one charge exists at the provider.
        var charges = await Factory.WithDbAsync(db => db.MockProviderTransactions.CountAsync(t => t.Kind == "charge" && t.PaymentMethodToken == "pm_test_timeout" && t.AmountMinor == 10_000));
        charges.ShouldBeGreaterThanOrEqualTo(1);
        var attemptsAtProvider = await Factory.WithDbAsync(async db =>
        {
            var attemptIds = await db.PaymentAttempts.IgnoreQueryFilters().Where(a => a.PaymentId == payment).Select(a => a.Id).ToListAsync();
            var keys = attemptIds.Select(id => id.ToString("N")).ToList();
            return await db.MockProviderTransactions.CountAsync(t => keys.Contains(t.IdempotencyKey));
        });
        attemptsAtProvider.ShouldBe(1);
    }

    [Fact]
    public async Task Transient_error_is_retried_with_a_new_attempt_and_succeeds()
    {
        var owner = await NewTenantOwnerAsync();
        var order = await owner.CreateSimpleOrderAsync();
        var payment = await owner.PayOkAsync(order, "pm_test_error");

        await WaitUntilAsync(async () => await owner.PaymentStatusAsync(payment) == "Succeeded");
        var attempts = await owner.GetJsonAsync($"/api/v1/payments/{payment}/attempts");
        attempts.GetArrayLength().ShouldBe(2);
        attempts[0].GetProperty("status").GetString().ShouldBe("Failed");
        attempts[1].GetProperty("trigger").GetString().ShouldBe("AutomaticRetry");
    }

    [Fact]
    public async Task Persistent_errors_fail_after_the_retry_budget_and_can_be_retried_manually()
    {
        var owner = await NewTenantOwnerAsync();
        var order = await owner.CreateSimpleOrderAsync();
        var payment = await owner.PayOkAsync(order, "pm_test_error_persistent");

        await WaitUntilAsync(async () => await owner.PaymentStatusAsync(payment) == "Failed");
        var failed = await owner.GetJsonAsync($"/api/v1/payments/{payment}");
        failed.GetProperty("failureCode").GetString().ShouldBe("provider_unavailable");
        failed.GetProperty("attemptCount").GetInt32().ShouldBe(3);

        var retry = await owner.PostWithKeyAsync($"/api/v1/payments/{payment}/retry", new { paymentMethodToken = "pm_test_success" }, "retry-1");
        retry.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await WaitUntilAsync(async () => await owner.PaymentStatusAsync(payment) == "Succeeded");
        (await owner.OrderStatusAsync(order)).ShouldBe("Paid");
    }

    [Fact]
    public async Task Pending_payment_is_completed_by_a_signed_webhook_and_duplicates_are_ignored()
    {
        var owner = await NewTenantOwnerAsync();
        var order = await owner.CreateSimpleOrderAsync();
        var payment = await owner.PayOkAsync(order, "pm_test_pending");

        string? reference = null;
        await WaitUntilAsync(async () =>
        {
            var attempts = await owner.GetJsonAsync($"/api/v1/payments/{payment}/attempts");
            reference = attempts[0].GetProperty("providerReference").GetString();
            return attempts[0].GetProperty("status").GetString() == "ProviderPending";
        });

        var body = JsonSerializer.Serialize(new
        {
            id = "evt_" + Guid.NewGuid().ToString("N"),
            type = "charge.succeeded",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            data = new { providerReference = reference, amountMinor = 10_000, currency = "TRY" },
        });

        var first = await PostWebhookAsync(body, PayFlowApiFactory.WebhookSecret);
        var duplicate = await PostWebhookAsync(body, PayFlowApiFactory.WebhookSecret);

        (await first.Content.ReadAsStringAsync(Ct)).ShouldContain("Processed");
        (await duplicate.Content.ReadAsStringAsync(Ct)).ShouldContain("Duplicate");
        (await owner.PaymentStatusAsync(payment)).ShouldBe("Succeeded");
        (await owner.OrderStatusAsync(order)).ShouldBe("Paid");
    }

    [Fact]
    public async Task Webhooks_with_bad_signatures_or_mismatched_amounts_are_rejected()
    {
        var owner = await NewTenantOwnerAsync();
        var order = await owner.CreateSimpleOrderAsync();
        var payment = await owner.PayOkAsync(order, "pm_test_pending");
        string? reference = null;
        await WaitUntilAsync(async () =>
        {
            reference = (await owner.GetJsonAsync($"/api/v1/payments/{payment}/attempts"))[0].GetProperty("providerReference").GetString();
            return reference is not null;
        });

        var tampered = JsonSerializer.Serialize(new { id = "evt_bad_sig", type = "charge.succeeded", data = new { providerReference = reference, amountMinor = 10_000, currency = "TRY" } });
        (await PostWebhookAsync(tampered, "wrong-secret")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var wrongAmount = JsonSerializer.Serialize(new { id = "evt_amount", type = "charge.succeeded", data = new { providerReference = reference, amountMinor = 1, currency = "TRY" } });
        var response = await PostWebhookAsync(wrongAmount, PayFlowApiFactory.WebhookSecret);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Rejected");
        (await owner.PaymentStatusAsync(payment)).ShouldBe("Pending");
    }

    [Fact]
    public async Task Cancelling_an_order_cancels_its_not_yet_processed_payment_but_paid_orders_cannot_be_cancelled()
    {
        var owner = await NewTenantOwnerAsync();
        var paidOrder = await owner.CreateSimpleOrderAsync();
        var payment = await owner.PayOkAsync(paidOrder);
        await WaitUntilAsync(async () => await owner.OrderStatusAsync(paidOrder) == "Paid");

        var cancelPaid = await owner.Http.PatchAsync($"/api/v1/orders/{paidOrder}/cancel", null, Ct);
        cancelPaid.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var draft = await owner.CreateSimpleOrderAsync();
        (await owner.Http.PatchAsync($"/api/v1/orders/{draft}/cancel", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.PayAsync(draft, "pm_test_success")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await owner.PaymentStatusAsync(payment)).ShouldBe("Succeeded");
    }

    private Task<HttpResponseMessage> PostWebhookAsync(string body, string secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments/mock")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(WebhookSignature.HeaderName, WebhookSignature.Create(secret, DateTimeOffset.UtcNow, body));
        return Factory.CreateClient().SendAsync(request, Ct);
    }
}

public sealed class IdempotencyTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Same_key_and_body_replays_the_original_response()
    {
        var owner = await NewTenantOwnerAsync();
        var order = await owner.CreateSimpleOrderAsync();

        var first = await owner.PayAsync(order, "pm_test_success", "key-1");
        var second = await owner.PayAsync(order, "pm_test_success", "key-1");

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        second.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        second.Headers.GetValues("Idempotency-Replayed").Single().ShouldBe("true");
        (await second.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("paymentId").GetGuid()
            .ShouldBe((await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("paymentId").GetGuid());
        (await owner.GetJsonAsync($"/api/v1/orders/{order}/payments")).GetProperty("totalCount").GetInt64().ShouldBe(1);
    }

    [Fact]
    public async Task Same_key_with_a_different_body_is_a_conflict()
    {
        var owner = await NewTenantOwnerAsync();
        var order = await owner.CreateSimpleOrderAsync();
        await owner.PayAsync(order, "pm_test_success", "key-2");

        var reused = await owner.PayAsync(order, "pm_test_decline", "key-2");

        reused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await reused.ProblemCodeAsync()).ShouldBe("idempotency_key_reused");
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_key_create_exactly_one_payment()
    {
        var owner = await NewTenantOwnerAsync();
        var order = await owner.CreateSimpleOrderAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => owner.PayAsync(order, "pm_test_success", "concurrent-key")));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Accepted || r.StatusCode == HttpStatusCode.Conflict);
        var ids = new HashSet<Guid>();
        foreach (var response in responses.Where(r => r.StatusCode == HttpStatusCode.Accepted))
        {
            ids.Add((await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("paymentId").GetGuid());
        }

        ids.Count.ShouldBe(1);
        (await owner.GetJsonAsync($"/api/v1/orders/{order}/payments")).GetProperty("totalCount").GetInt64().ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_requests_with_different_keys_still_cannot_double_charge_an_order()
    {
        var owner = await NewTenantOwnerAsync();
        var order = await owner.CreateSimpleOrderAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => owner.PayAsync(order, "pm_test_success", $"k-{i}")));

        responses.Count(r => r.StatusCode == HttpStatusCode.Accepted).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(5);
    }

    [Fact]
    public async Task Payment_commands_require_an_idempotency_key()
    {
        var owner = await NewTenantOwnerAsync();
        var order = await owner.CreateSimpleOrderAsync();
        var response = await owner.Http.PostAsJsonAsync($"/api/v1/orders/{order}/payments", new { paymentMethodToken = "pm_test_success" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Card_data_or_malformed_tokens_are_never_accepted()
    {
        var owner = await NewTenantOwnerAsync();
        var order = await owner.CreateSimpleOrderAsync();
        (await owner.PayAsync(order, "4111111111111111")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}

public sealed class RefundTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    private async Task<(TestSession Owner, Guid Payment)> PaidAsync(long amount)
    {
        var owner = await NewTenantOwnerAsync();
        var order = await owner.CreateSimpleOrderAsync(priceMinor: amount);
        var payment = await owner.PayOkAsync(order);
        await WaitUntilAsync(async () => await owner.PaymentStatusAsync(payment) == "Succeeded");
        return (owner, payment);
    }

    [Fact]
    public async Task Partial_then_full_refund_updates_payment_status()
    {
        var (owner, payment) = await PaidAsync(10_000);

        (await owner.PostWithKeyAsync($"/api/v1/payments/{payment}/refunds", new { amountMinor = 4_000, reason = "damaged" }, "r1")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await WaitUntilAsync(async () => await owner.PaymentStatusAsync(payment) == "PartiallyRefunded");

        (await owner.PostWithKeyAsync($"/api/v1/payments/{payment}/refunds", new { amountMinor = 6_000 }, "r2")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await WaitUntilAsync(async () => await owner.PaymentStatusAsync(payment) == "Refunded");

        var refunds = await owner.GetJsonAsync($"/api/v1/payments/{payment}/refunds");
        refunds.EnumerateArray().ShouldAllBe(r => r.GetProperty("status").GetString() == "Succeeded");
    }

    [Fact]
    public async Task Refund_larger_than_the_refundable_amount_is_rejected()
    {
        var (owner, payment) = await PaidAsync(10_000);
        var response = await owner.PostWithKeyAsync($"/api/v1/payments/{payment}/refunds", new { amountMinor = 10_001 }, "too-much");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ProblemCodeAsync()).ShouldBe("refund_amount_exceeds_refundable");
    }

    [Fact]
    public async Task Concurrent_refunds_never_exceed_the_captured_amount()
    {
        var (owner, payment) = await PaidAsync(10_000);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(i => owner.PostWithKeyAsync($"/api/v1/payments/{payment}/refunds", new { amountMinor = 3_000 }, $"c-{i}")));

        responses.Count(r => r.StatusCode == HttpStatusCode.Accepted).ShouldBe(3);
        responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest).ShouldBe(3);
        await WaitUntilAsync(async () => (await owner.GetJsonAsync($"/api/v1/payments/{payment}")).GetProperty("refundedAmountMinor").GetInt64() == 9_000);
        (await owner.PaymentStatusAsync(payment)).ShouldBe("PartiallyRefunded");
    }

    [Fact]
    public async Task Failed_payments_cannot_be_refunded()
    {
        var owner = await NewTenantOwnerAsync();
        var order = await owner.CreateSimpleOrderAsync();
        var payment = await owner.PayOkAsync(order, "pm_test_decline");
        await WaitUntilAsync(async () => await owner.PaymentStatusAsync(payment) == "Failed");

        var response = await owner.PostWithKeyAsync($"/api/v1/payments/{payment}/refunds", new { amountMinor = 100 }, "r");
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Non_positive_refund_amounts_are_rejected(long amount)
    {
        var (owner, payment) = await PaidAsync(10_000);
        (await owner.PostWithKeyAsync($"/api/v1/payments/{payment}/refunds", new { amountMinor = amount }, $"neg-{amount}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}

public sealed class OrderTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Prices_come_from_the_catalog_and_snapshots_survive_price_changes()
    {
        var owner = await NewTenantOwnerAsync();
        var customer = await owner.CreateCustomerAsync();
        var product = await owner.CreateProductAsync(priceMinor: 2_500);

        // A client-supplied price/total is ignored (not part of the contract).
        var created = await owner.PostOkAsync("/api/v1/orders", new
        {
            customerId = customer,
            items = new[] { new { productId = product, quantity = 4, unitPriceMinor = 1 } },
            totalMinor = 1,
        });
        created.GetProperty("totalMinor").GetInt64().ShouldBe(10_000);

        await owner.Http.PutAsJsonAsync($"/api/v1/products/{product}", new { name = "Renamed", priceMinor = 9_999, currency = "TRY" }, Ct);

        var order = await owner.GetJsonAsync($"/api/v1/orders/{created.GetProperty("id").GetGuid()}");
        order.GetProperty("totalMinor").GetInt64().ShouldBe(10_000);
        order.GetProperty("items")[0].GetProperty("unitPriceMinor").GetInt64().ShouldBe(2_500);
        order.GetProperty("items")[0].GetProperty("name").GetString().ShouldBe("Product");
    }

    [Fact]
    public async Task Inactive_products_and_invalid_quantities_are_rejected()
    {
        var owner = await NewTenantOwnerAsync();
        var customer = await owner.CreateCustomerAsync();
        var product = await owner.CreateProductAsync();
        await owner.Http.PatchAsJsonAsync($"/api/v1/products/{product}/status", new { status = "Inactive" }, Ct);

        var inactive = await owner.Http.PostAsJsonAsync("/api/v1/orders", new { customerId = customer, items = new[] { new { productId = product, quantity = 1 } } }, Ct);
        inactive.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var negative = await owner.Http.PostAsJsonAsync("/api/v1/orders", new { customerId = customer, items = new[] { new { productId = product, quantity = -1 } } }, Ct);
        negative.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Order_numbers_are_sequential_per_tenant_and_listing_supports_allowlisted_sort()
    {
        var owner = await NewTenantOwnerAsync();
        var customer = await owner.CreateCustomerAsync();
        var product = await owner.CreateProductAsync();
        var numbers = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            numbers.Add((await owner.CreateOrderAsync(customer, (product, 1))).GetProperty("orderNumber").GetString()!);
        }

        numbers.ShouldBe(["SO-000001", "SO-000002", "SO-000003"]);

        var page = await owner.GetJsonAsync("/api/v1/orders?sort=orderNumber:asc&pageSize=2");
        page.GetProperty("totalCount").GetInt64().ShouldBe(3);
        page.GetProperty("items").GetArrayLength().ShouldBe(2);
        page.GetProperty("items")[0].GetProperty("orderNumber").GetString().ShouldBe("SO-000001");

        (await owner.Http.GetAsync("/api/v1/orders?sort=password_hash", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Different_currencies_are_kept_apart_in_reports()
    {
        var owner = await NewTenantOwnerAsync();
        var customer = await owner.CreateCustomerAsync();
        var tryProduct = await owner.CreateProductAsync(10_000, "TRY");
        var usdProduct = await owner.CreateProductAsync(2_500, "USD");
        var tryOrder = (await owner.CreateOrderAsync(customer, (tryProduct, 1))).GetProperty("id").GetGuid();
        var usdOrder = (await owner.CreateOrderAsync(customer, (usdProduct, 2))).GetProperty("id").GetGuid();
        var p1 = await owner.PayOkAsync(tryOrder);
        var p2 = await owner.PayOkAsync(usdOrder);
        await WaitUntilAsync(async () => await owner.PaymentStatusAsync(p1) == "Succeeded" && await owner.PaymentStatusAsync(p2) == "Succeeded");

        var overview = await owner.GetJsonAsync("/api/v1/reports/overview");
        var currencies = overview.GetProperty("currencies").EnumerateArray().ToDictionary(c => c.GetProperty("currency").GetString()!, c => c);
        currencies["TRY"].GetProperty("grossPaymentAmountMinor").GetInt64().ShouldBe(10_000);
        currencies["USD"].GetProperty("grossPaymentAmountMinor").GetInt64().ShouldBe(5_000);
        currencies["USD"].GetProperty("paymentSuccessRate").GetDecimal().ShouldBe(1m);

        var mixed = await owner.Http.PostAsJsonAsync("/api/v1/orders", new { customerId = customer, items = new[] { new { productId = tryProduct, quantity = 1 }, new { productId = usdProduct, quantity = 1 } } }, Ct);
        mixed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
