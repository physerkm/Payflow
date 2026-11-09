using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PayFlow.Application.Assistant;
using PayFlow.IntegrationTests.Infrastructure;

namespace PayFlow.IntegrationTests;

public sealed class OrderHistoryTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task History_lists_creation_attempts_payment_and_refund_in_order()
    {
        var owner = await NewTenantOwnerAsync();
        var orderId = await owner.CreateSimpleOrderAsync(10_000);

        // pm_test_error fails the first attempt with a transient error; the automatic retry succeeds.
        var paymentId = await owner.PayOkAsync(orderId, "pm_test_error");
        await WaitUntilAsync(async () => await owner.OrderStatusAsync(orderId) == "Paid", because: "order paid after the retry");
        (await owner.PostWithKeyAsync($"/api/v1/payments/{paymentId}/refunds", new { amountMinor = 4_000, reason = "damaged" }, "history-r1"))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await WaitUntilAsync(async () => await owner.PaymentStatusAsync(paymentId) == "PartiallyRefunded");

        var history = (await owner.GetJsonAsync($"/api/v1/orders/{orderId}/history")).EnumerateArray().ToList();

        history.Select(e => e.GetProperty("type").GetString()).ShouldBe(
        [
            "order.created", "payment.initiated", "payment.attempt", "payment.attempt",
            "payment.succeeded", "order.paid", "refund.requested", "refund.succeeded",
        ]);

        var times = history.Select(e => e.GetProperty("occurredAt").GetDateTimeOffset()).ToList();
        times.ShouldBe(times.Order().ToList());

        var attempts = history.Where(e => e.GetProperty("type").GetString() == "payment.attempt").ToList();
        attempts[0].GetProperty("status").GetString().ShouldBe("Failed");
        attempts[0].GetProperty("code").GetString().ShouldNotBeNullOrEmpty();
        attempts[1].GetProperty("status").GetString().ShouldBe("Succeeded");
        attempts[1].GetProperty("trigger").GetString().ShouldBe("AutomaticRetry");

        var refund = history.Single(e => e.GetProperty("type").GetString() == "refund.requested");
        refund.GetProperty("amountMinor").GetInt64().ShouldBe(4_000);
        refund.GetProperty("note").GetString().ShouldBe("damaged");
        refund.GetProperty("paymentId").GetGuid().ShouldBe(paymentId);
    }

    [Fact]
    public async Task Another_tenants_order_history_is_not_found()
    {
        var tenantA = await NewTenantOwnerAsync();
        var orderId = await tenantA.CreateSimpleOrderAsync();
        var tenantB = await NewTenantOwnerAsync();

        (await tenantB.Http.GetAsync($"/api/v1/orders/{orderId}/history", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

/// <summary>A host whose AI model never answers and whose assistant time budget is half a second.</summary>
public sealed class RequestTimeoutFixture : ApiFixture
{
    protected override Task<IReadOnlyDictionary<string, string?>?> PrepareAsync(string connectionString) =>
        Task.FromResult<IReadOnlyDictionary<string, string?>?>(new Dictionary<string, string?>
        {
            ["RequestTimeouts:Assistant"] = "00:00:00.500",
        });

    protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton<IAssistantModel, HangingModel>();
}

public sealed class RequestTimeoutTests(RequestTimeoutFixture fixture) : IntegrationTest<RequestTimeoutFixture>(fixture)
{
    [Fact]
    public async Task Request_over_its_time_budget_is_cancelled_with_504()
    {
        var owner = await NewTenantOwnerAsync();
        var stopwatch = Stopwatch.StartNew();

        var response = await owner.Http.PostAsJsonAsync("/api/v1/assistant/ask", new { question = "How did payments go this week?" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        (await response.ProblemCodeAsync()).ShouldBe("request_timeout");
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));

        // The cancellation reached the slow dependency, so it stops working instead of running on in the background.
        ((HangingModel)Factory.Services.GetRequiredService<IAssistantModel>()).WasCancelled.ShouldBeTrue();
    }
}

internal sealed class HangingModel : IAssistantModel
{
    public bool IsConfigured => true;

    public string ModelName => "hanging-test-model";

    public bool WasCancelled { get; private set; }

    public async Task<AssistantModelResult> RunAsync(
        string systemPrompt,
        string userMessage,
        IReadOnlyList<AssistantToolSpec> tools,
        Func<AssistantToolCall, CancellationToken, Task<AssistantToolResult>> executeTool,
        int maxToolRounds,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            WasCancelled = true;
            throw;
        }

        throw new UnreachableException();
    }
}
