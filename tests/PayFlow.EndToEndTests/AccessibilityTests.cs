using System.Text.Json;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace PayFlow.EndToEndTests;

/// <summary>
/// Automated accessibility scan (spec §16): every main screen, two form dialogs and dark mode are checked with
/// axe-core against WCAG 2.1 level A and AA. The full report is written to e2e-artifacts/accessibility.json.
/// Automated rules catch a part of the problems only; keyboard and screen-reader checks stay manual.
/// </summary>
public sealed class AccessibilityTests : IAsyncLifetime
{
    private const string Password = "Str0ngPassw0rd";
    private static readonly string[] WcagTags = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];

    private readonly List<Finding> _findings = [];
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async ValueTask InitializeAsync()
    {
        if (E2E.Disabled)
        {
            return;
        }

        _playwright = await Playwright.CreateAsync();
        _browser = await E2E.LaunchAsync(_playwright);
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
    }

    [Fact]
    public async Task Main_screens_meet_wcag_2_1_aa_automated_checks()
    {
        Assert.SkipWhen(E2E.Disabled, E2E.SkipReason);
        var data = await SeedAsync();

        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = E2E.BaseUrl,
            Locale = "en-US",
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            ColorScheme = ColorScheme.Light,
        });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(15_000);

        await ScanAsync(page, "/register", "Create your account");
        await ScanAsync(page, "/login", "Sign in");

        await page.GetByLabel("Email").FillAsync(data.Email);
        await page.GetByLabel("Password").FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Dashboard" })).ToBeVisibleAsync();

        await ScanAsync(page, "/", "Dashboard");
        await ScanAsync(page, "/customers", "Customers");
        await ScanAsync(page, "/products", "Products");
        await ScanAsync(page, "/orders", "Orders");
        await page.GetByRole(AriaRole.Button, new() { Name = "New order" }).ClickAsync();
        await OpenedDialogAsync(page);
        await CheckAsync(page, "/orders (new order dialog)");
        await page.Keyboard.PressAsync("Escape");
        await ScanAsync(page, $"/orders/{data.OrderId}", "SO-000001");
        await ScanAsync(page, "/payments", "Payments");
        await ScanAsync(page, $"/payments/{data.PaymentId}", null);
        await page.GetByRole(AriaRole.Button, new() { Name = "Refund" }).ClickAsync();
        await OpenedDialogAsync(page);
        await CheckAsync(page, "/payments/{id} (refund dialog)");
        await page.Keyboard.PressAsync("Escape");
        await ScanAsync(page, "/refunds", "Refunds");
        await ScanAsync(page, "/reports", "Reports");
        await ScanAsync(page, "/team", "Team");
        await ScanAsync(page, "/settings", "Settings");
        await ScanAsync(page, "/assistant", "AI assistant");

        // Dark mode has its own palette, so contrast is checked again.
        await page.GetByRole(AriaRole.Button, new() { Name = "Toggle color mode" }).ClickAsync();
        await ScanAsync(page, "/", "Dashboard", "dark");
        await ScanAsync(page, $"/orders/{data.OrderId}", "SO-000001", "dark");
        await ScanAsync(page, "/reports", "Reports", "dark");
        await context.CloseAsync();

        await File.WriteAllTextAsync(
            E2E.Artifact("accessibility.json"),
            JsonSerializer.Serialize(_findings, new JsonSerializerOptions { WriteIndented = true }),
            TestContext.Current.CancellationToken);

        _findings.ShouldBeEmpty(string.Join(
            Environment.NewLine,
            _findings.Select(f => $"{f.Page}: [{f.Impact}] {f.Rule} - {f.Help} -> {string.Join(" | ", f.Targets)}")));
    }

    private async Task ScanAsync(IPage page, string path, string? heading, string mode = "light")
    {
        await page.GotoAsync(path);
        var title = heading is null
            ? page.GetByRole(AriaRole.Heading, new() { Level = 1 })
            : page.GetByRole(AriaRole.Heading, new() { Name = heading });
        await Assertions.Expect(title.First).ToBeVisibleAsync();
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5_000 });
        }
        catch (TimeoutException)
        {
            // Pages that poll (e.g. a payment in progress) never go idle; the content is already rendered.
        }

        await page.WaitForTimeoutAsync(800); // chart entry animations
        await CheckAsync(page, mode == "light" ? path : $"{path} ({mode})");
    }

    /// <summary>Waits until the dialog has finished its fade-in, so colors are measured at full opacity.</summary>
    private static async Task OpenedDialogAsync(IPage page)
    {
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToBeVisibleAsync();
        await page.WaitForTimeoutAsync(600);
    }

    private async Task CheckAsync(IPage page, string label)
    {
        var result = await page.RunAxe(new AxeRunOptions
        {
            RunOnly = new RunOnlyOptions { Type = "tag", Values = [.. WcagTags] },
        });
        _findings.AddRange(result.Violations.Select(v => new Finding(
            label,
            v.Id,
            v.Impact ?? "unknown",
            v.Help,
            v.Nodes.Select(n => n.Target?.ToString() ?? n.Html).Take(5).ToList(),
            v.Nodes.Select(n => $"{n.Html} :: {string.Join("; ", n.Any.Concat(n.All).Concat(n.None).Select(c => c.Message))}").Take(5).ToList())));
    }

    /// <summary>Creates a company with a paid, partially refunded order through the API.</summary>
    private async Task<SeedData> SeedAsync()
    {
        await using var api = await _playwright!.APIRequest.NewContextAsync(new() { BaseURL = E2E.BaseUrl });
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var email = $"a11y+{suffix}@payflow.test";

        var token = (await PostAsync(api, "/api/v1/auth/register", new { email, password = Password, displayName = "Ada Lovelace" })).GetProperty("accessToken").GetString()!;
        var tenant = await PostAsync(api, "/api/v1/tenants", new { name = $"A11y {suffix}", slug = $"a11y-{suffix}", defaultCurrency = "TRY", timeZone = "Europe/Istanbul" }, token);
        token = (await PostAsync(api, "/api/v1/auth/activate-tenant", new { tenantId = tenant.GetProperty("id").GetGuid() }, token)).GetProperty("accessToken").GetString()!;

        var customer = await PostAsync(api, "/api/v1/customers", new { name = "Grace Hopper", email = $"grace+{suffix}@example.com" }, token);
        var product = await PostAsync(api, "/api/v1/products", new { sku = "PRO-PLAN", name = "Pro Plan", priceMinor = 150075, currency = "TRY" }, token);
        var order = await PostAsync(api, "/api/v1/orders", new
        {
            customerId = customer.GetProperty("id").GetGuid(),
            items = new[] { new { productId = product.GetProperty("id").GetGuid(), quantity = 2 } },
        }, token);
        var orderId = order.GetProperty("id").GetGuid();
        var paymentId = (await PostAsync(api, $"/api/v1/orders/{orderId}/payments", new { paymentMethodToken = "pm_test_success" }, token, key: $"a11y-pay-{suffix}"))
            .GetProperty("paymentId").GetGuid();

        await WaitForStatusAsync(api, $"/api/v1/payments/{paymentId}", token, "Succeeded");
        await PostAsync(api, $"/api/v1/payments/{paymentId}/refunds", new { amountMinor = 50_000, reason = "Damaged item" }, token, key: $"a11y-refund-{suffix}");
        await WaitForStatusAsync(api, $"/api/v1/payments/{paymentId}", token, "PartiallyRefunded");
        await PostAsync(api, "/api/v1/tenants/current/invitations", new { email = $"finance+{suffix}@payflow.test", role = "Finance" }, token);
        return new SeedData(email, orderId, paymentId);
    }

    private static async Task<JsonElement> PostAsync(IAPIRequestContext api, string path, object body, string? token = null, string? key = null)
    {
        var headers = new Dictionary<string, string>();
        if (token is not null)
        {
            headers["Authorization"] = $"Bearer {token}";
        }

        if (key is not null)
        {
            headers["Idempotency-Key"] = key;
        }

        var response = await api.PostAsync(path, new() { DataObject = body, Headers = headers });
        response.Ok.ShouldBeTrue($"POST {path} returned {response.Status}: {await response.TextAsync()}");
        return (await response.JsonAsync())!.Value;
    }

    private static async Task WaitForStatusAsync(IAPIRequestContext api, string path, string token, string status)
    {
        for (var i = 0; i < 60; i++)
        {
            var response = await api.GetAsync(path, new() { Headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" } });
            if ((await response.JsonAsync())!.Value.GetProperty("status").GetString() == status)
            {
                return;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"{path} did not reach {status}.");
    }

    private sealed record SeedData(string Email, Guid OrderId, Guid PaymentId);

    private sealed record Finding(string Page, string Rule, string Impact, string Help, IReadOnlyList<string> Targets, IReadOnlyList<string> Details);
}
