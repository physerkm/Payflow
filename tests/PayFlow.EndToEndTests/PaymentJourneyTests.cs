using Microsoft.Playwright;

namespace PayFlow.EndToEndTests;

/// <summary>
/// Browser test of the main user journey: register → company → customer → product → order → payment → refund.
/// Runs against a running stack (docker compose or local dev):
/// <code>
/// PAYFLOW_E2E_BASE_URL=http://localhost:8080 dotnet test tests/PayFlow.EndToEndTests
/// </code>
/// Optional: PAYFLOW_E2E_BROWSER_CHANNEL=msedge|chrome to use an installed browser instead of Playwright's Chromium,
/// PAYFLOW_E2E_HEADED=1 to watch it.
/// </summary>
public sealed class PaymentJourneyTests : IAsyncLifetime
{
    private static readonly string? BaseUrl = E2E.BaseUrl;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async ValueTask InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
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
    public async Task User_can_register_sell_collect_a_payment_and_refund_it()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(BaseUrl), "Set PAYFLOW_E2E_BASE_URL to run browser tests against a running stack.");
        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions { BaseURL = BaseUrl, Locale = "en-US", TimezoneId = "Europe/Istanbul", ViewportSize = new ViewportSize { Width = 1440, Height = 900 } });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(15_000);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // Register
        await page.GotoAsync("/register");
        await page.GetByLabel("Full name").FillAsync("Ayşe Demir");
        await page.GetByLabel("Work email").FillAsync($"ayse+{suffix}@payflow.test");
        await page.GetByLabel("Password").FillAsync("Str0ngPassw0rd");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();
        await page.WaitForURLAsync("**/tenants");

        // Create the company (tenant) - the user becomes its Owner
        await page.GetByLabel("Company name").FillAsync($"Acme {suffix}");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create company" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Dashboard" })).ToBeVisibleAsync();

        // Customer
        await page.GetByRole(AriaRole.Link, new() { Name = "Customers" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "New customer" }).ClickAsync();
        var customerDialog = page.GetByRole(AriaRole.Dialog);
        await customerDialog.GetByLabel("Name").FillAsync("Mehmet Yılmaz");
        await customerDialog.GetByLabel("Email").FillAsync($"mehmet+{suffix}@example.com");
        await customerDialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Gridcell, new() { Name = "Mehmet Yılmaz", Exact = true })).ToBeVisibleAsync();

        // Product (price typed in major units, sent as minor units)
        await page.GetByRole(AriaRole.Link, new() { Name = "Products" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "New product" }).ClickAsync();
        var productDialog = page.GetByRole(AriaRole.Dialog);
        await productDialog.GetByLabel("SKU").FillAsync("PRO-PLAN");
        await productDialog.GetByLabel("Name").FillAsync("Pro Plan");
        await productDialog.GetByLabel("Price").FillAsync("1500.75");
        await productDialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Gridcell, new() { Name = "PRO-PLAN", Exact = true })).ToBeVisibleAsync();

        // Order with two units; the server computes 3,001.50
        await page.GetByRole(AriaRole.Link, new() { Name = "Orders" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "New order" }).ClickAsync();
        var orderDialog = page.GetByRole(AriaRole.Dialog);
        await orderDialog.GetByLabel("Customer").FillAsync("Mehmet");
        await page.GetByRole(AriaRole.Option).First.ClickAsync();
        await orderDialog.GetByLabel("Product 1").FillAsync("PRO");
        await page.GetByRole(AriaRole.Option).First.ClickAsync();
        await orderDialog.GetByLabel("Qty").FillAsync("2");
        await orderDialog.GetByRole(AriaRole.Button, new() { Name = "Create order" }).ClickAsync();
        await page.WaitForURLAsync("**/orders/*");
        await Assertions.Expect(page.GetByText("SO-000001").First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("3,001.50").First).ToBeVisibleAsync();
        await page.ScreenshotAsync(new() { Path = Artifact("order-detail.png"), FullPage = true });

        // Asynchronous payment: 202 Accepted, then the page polls until the worker finishes
        await page.GetByRole(AriaRole.Button, new() { Name = "Take payment" }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Pay" }).ClickAsync();
        await page.WaitForURLAsync("**/payments/*");
        await Assertions.Expect(page.GetByText("Succeeded").First).ToBeVisibleAsync(new() { Timeout = 30_000 });

        // Partial refund
        await page.GetByRole(AriaRole.Button, new() { Name = "Refund" }).ClickAsync();
        var refundDialog = page.GetByRole(AriaRole.Dialog);
        await refundDialog.GetByLabel("Amount (TRY)").FillAsync("1000");
        await refundDialog.GetByRole(AriaRole.Button, new() { Name = "Refund" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Partially refunded").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await page.ScreenshotAsync(new() { Path = Artifact("payment-detail.png"), FullPage = true });

        // Dashboard reflects the payment and the refund
        await page.GetByRole(AriaRole.Link, new() { Name = "Dashboard" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Net collected (TRY)")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("2,001.50").First).ToBeVisibleAsync();
        await page.WaitForTimeoutAsync(1500); // let chart entry animations finish before the screenshot
        await page.ScreenshotAsync(new() { Path = Artifact("dashboard.png"), FullPage = true });

        // Reports (per-currency table view backs every chart)
        await page.GetByRole(AriaRole.Link, new() { Name = "Reports" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Summary by currency")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Table, new() { Name = "Financial summary by currency" })).ToContainTextAsync("2,001.50");

        // AI assistant page renders (enabled only when the server has an API key)
        await page.GetByRole(AriaRole.Link, new() { Name = "AI assistant" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "AI assistant" })).ToBeVisibleAsync();

        // Dark mode
        await page.GetByRole(AriaRole.Link, new() { Name = "Reports" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Toggle color mode" }).ClickAsync();
        await page.WaitForTimeoutAsync(1500);
        await page.ScreenshotAsync(new() { Path = Artifact("reports-dark.png"), FullPage = true });
        await context.CloseAsync();
    }

    /// <summary>Needs PAYFLOW_E2E_PLATFORM_ADMIN = an email listed in the server's Platform:Admins.</summary>
    [Fact]
    public async Task Platform_admin_can_suspend_and_reactivate_a_tenant()
    {
        var adminEmail = Environment.GetEnvironmentVariable("PAYFLOW_E2E_PLATFORM_ADMIN");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(adminEmail), "Set PAYFLOW_E2E_BASE_URL and PAYFLOW_E2E_PLATFORM_ADMIN.");
        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions { BaseURL = BaseUrl, Locale = "en-US", ViewportSize = new ViewportSize { Width = 1440, Height = 900 } });
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // A tenant to manage, created through a separate API context (its cookies must not log the browser in).
        await using var api = await _playwright!.APIRequest.NewContextAsync(new() { BaseURL = BaseUrl });
        var owner = await api.PostAsync("/api/v1/auth/register", new() { DataObject = new { email = $"owner+{suffix}@payflow.test", password = "Str0ngPassw0rd", displayName = "Owner" } });
        var ownerToken = (await owner.JsonAsync())!.Value.GetProperty("accessToken").GetString();
        await api.PostAsync("/api/v1/tenants", new()
        {
            Headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {ownerToken}" },
            DataObject = new { name = $"Suspendable {suffix}", slug = $"susp-{suffix}", defaultCurrency = "TRY", timeZone = "Europe/Istanbul" },
        });

        // The platform account may already exist from an earlier run.
        await api.PostAsync("/api/v1/auth/register", new() { DataObject = new { email = adminEmail, password = "Str0ngPassw0rd", displayName = "Platform Admin" } });

        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(15_000);
        await page.GotoAsync("/login");
        await page.GetByLabel("Email").FillAsync(adminEmail!);
        await page.GetByLabel("Password").FillAsync("Str0ngPassw0rd");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await page.WaitForURLAsync(url => !url.Contains("/login", StringComparison.Ordinal));
        await page.GotoAsync("/platform");

        await page.GetByLabel("Search name or slug").FillAsync($"susp-{suffix}");
        var row = page.GetByRole(AriaRole.Row).Filter(new() { HasText = $"susp-{suffix}" });
        await row.GetByRole(AriaRole.Button, new() { Name = "Suspend" }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByLabel("Reason (audited)").FillAsync("E2E check");
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Confirm" }).ClickAsync();
        await Assertions.Expect(row.GetByText("Suspended")).ToBeVisibleAsync();
        await page.ScreenshotAsync(new() { Path = Artifact("platform.png"), FullPage = true });

        await row.GetByRole(AriaRole.Button, new() { Name = "Reactivate" }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByLabel("Reason (audited)").FillAsync("E2E done");
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Confirm" }).ClickAsync();
        await Assertions.Expect(row.GetByText("Active")).ToBeVisibleAsync();
        await context.CloseAsync();
    }

    [Fact]
    public async Task Unauthenticated_users_are_redirected_to_sign_in()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(BaseUrl), "Set PAYFLOW_E2E_BASE_URL to run browser tests against a running stack.");
        var page = await _browser!.NewPageAsync(new BrowserNewPageOptions { BaseURL = BaseUrl });
        await page.GotoAsync("/payments");
        await page.WaitForURLAsync("**/login");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Sign in" })).ToBeVisibleAsync();
    }

    private static string Artifact(string name) => E2E.Artifact(name);
}
