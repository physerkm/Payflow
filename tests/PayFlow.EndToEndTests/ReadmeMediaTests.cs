using Microsoft.Playwright;

namespace PayFlow.EndToEndTests;

/// <summary>
/// Regenerates the README screenshots and the demo video from the seeded demo company. Opt-in:
/// <code>
/// node scripts/seed-demo.mjs
/// PAYFLOW_E2E_BASE_URL=http://localhost:8080 PAYFLOW_E2E_CAPTURE_DIR=$PWD/docs \
///   dotnet test tests/PayFlow.EndToEndTests --filter ReadmeMediaTests
/// </code>
/// The Grafana screenshot needs the observability profile (PAYFLOW_E2E_GRAFANA_URL, default http://localhost:3000).
/// </summary>
public sealed class ReadmeMediaTests : IAsyncLifetime
{
    private const string DemoEmail = "demo@payflow.local";
    private const string DemoPassword = "PayFlow2026";
    private static readonly string? CaptureDir = Environment.GetEnvironmentVariable("PAYFLOW_E2E_CAPTURE_DIR");
    private static readonly string GrafanaUrl = Environment.GetEnvironmentVariable("PAYFLOW_E2E_GRAFANA_URL") ?? "http://localhost:3000";

    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async ValueTask InitializeAsync()
    {
        if (E2E.Disabled || string.IsNullOrWhiteSpace(CaptureDir))
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
    public async Task Capture_readme_screenshots_and_demo_video()
    {
        Assert.SkipWhen(E2E.Disabled || string.IsNullOrWhiteSpace(CaptureDir), "Set PAYFLOW_E2E_BASE_URL and PAYFLOW_E2E_CAPTURE_DIR to refresh the README media.");
        var images = Directory.CreateDirectory(Path.Combine(CaptureDir!, "images")).FullName;
        var media = Directory.CreateDirectory(Path.Combine(CaptureDir!, "media")).FullName;

        await CaptureScreenshotsAsync(images);
        await CaptureGrafanaAsync(images);
        await RecordDemoAsync(media);
    }

    private async Task CaptureScreenshotsAsync(string images)
    {
        var context = await _browser!.NewContextAsync(Options(new ViewportSize { Width = 1440, Height = 900 }));
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        await SignInAsync(page, typing: false);

        await Settle(page);
        await page.ScreenshotAsync(new() { Path = Path.Combine(images, "dashboard.png") });

        await page.GetByRole(AriaRole.Link, new() { Name = "Orders" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Gridcell, new() { Name = "SO-000004", Exact = true })).ToBeVisibleAsync();
        await Settle(page);
        await page.ScreenshotAsync(new() { Path = Path.Combine(images, "orders.png") });

        // SO-000004 used pm_test_error: a failed first attempt, an automatic retry, then success.
        await page.GetByRole(AriaRole.Gridcell, new() { Name = "SO-000004", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.List, new() { Name = "Order history" })).ToBeVisibleAsync();
        await Settle(page);
        await page.ScreenshotAsync(new() { Path = Path.Combine(images, "order-history.png"), FullPage = true });

        await page.GetByRole(AriaRole.Link, new() { Name = "Payments" }).ClickAsync();
        await page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Partially refunded" }).First.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Attempts" })).ToBeVisibleAsync();
        await Settle(page);
        await page.ScreenshotAsync(new() { Path = Path.Combine(images, "payment-detail.png"), FullPage = true });

        await page.GetByRole(AriaRole.Link, new() { Name = "Reports" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Summary by currency")).ToBeVisibleAsync();
        await Settle(page);
        await page.ScreenshotAsync(new() { Path = Path.Combine(images, "reports.png"), FullPage = true });

        await page.GetByRole(AriaRole.Link, new() { Name = "Dashboard" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Toggle color mode" }).ClickAsync();
        await Settle(page);
        await page.ScreenshotAsync(new() { Path = Path.Combine(images, "dashboard-dark.png") });
        await context.CloseAsync();
    }

    private async Task CaptureGrafanaAsync(string images)
    {
        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1600, Height = 2150 }, ColorScheme = ColorScheme.Dark });
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync($"{GrafanaUrl}/d/payflow-overview?orgId=1&kiosk&from=now-1h&to=now");
            // Grafana loads only panels inside the viewport, hence the tall window; panels query Prometheus after load.
            await page.WaitForTimeoutAsync(8_000);
            await page.ScreenshotAsync(new() { Path = Path.Combine(images, "grafana.png") });
        }
        catch (PlaywrightException)
        {
            // Observability profile not running: keep the previous Grafana screenshot.
        }

        await context.CloseAsync();
    }

    /// <summary>About a minute: sign in, create an order, take a payment, refund part of it, read the history.</summary>
    private async Task RecordDemoAsync(string media)
    {
        var videoDir = Directory.CreateTempSubdirectory("payflow-video").FullName;
        var size = new ViewportSize { Width = 1280, Height = 800 };
        var options = Options(size);
        options.RecordVideoDir = videoDir;
        options.RecordVideoSize = new RecordVideoSize { Width = size.Width, Height = size.Height };
        var context = await _browser!.NewContextAsync(options);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(30_000);

        await SignInAsync(page, typing: true);
        await Pause(page, 2_500);

        await page.GetByRole(AriaRole.Link, new() { Name = "Orders", Exact = true }).ClickAsync();
        await Pause(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "New order" }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.GetByLabel("Customer").PressSequentiallyAsync("Ayşe", new() { Delay = 80 });
        await page.GetByRole(AriaRole.Option, new() { Name = "Ayşe Yılmaz" }).First.ClickAsync();
        await dialog.GetByLabel("Product 1").PressSequentiallyAsync("Pro", new() { Delay = 80 });
        await page.GetByRole(AriaRole.Option, new() { Name = "Pro Plan" }).First.ClickAsync();
        await Pause(page);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Create order" }).ClickAsync();
        await page.WaitForURLAsync("**/orders/*");
        await Pause(page, 2_000);

        await page.GetByRole(AriaRole.Button, new() { Name = "Take payment" }).ClickAsync();
        await Pause(page, 2_000); // the dialog lists the simulated provider's test tokens
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Pay" }).ClickAsync();
        await page.WaitForURLAsync("**/payments/*");
        await Assertions.Expect(page.GetByText("Succeeded").First).ToBeVisibleAsync();
        await Pause(page, 2_500);

        await page.GetByRole(AriaRole.Button, new() { Name = "Refund", Exact = true }).ClickAsync();
        var amount = page.GetByRole(AriaRole.Dialog).GetByLabel("Amount");
        await amount.ClearAsync();
        await amount.PressSequentiallyAsync("500", new() { Delay = 120 });
        await Pause(page);
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Refund" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Partially refunded").First).ToBeVisibleAsync();
        await Pause(page, 2_500);

        await page.GetByRole(AriaRole.Link, new() { Name = "SO-" }).First.ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "History" }).ScrollIntoViewIfNeededAsync();
        await Pause(page, 3_000);

        await page.GetByRole(AriaRole.Link, new() { Name = "Reports" }).ClickAsync();
        await Pause(page, 2_500);
        await page.GetByRole(AriaRole.Button, new() { Name = "Toggle color mode" }).ClickAsync();
        await Pause(page, 2_500);

        var video = page.Video!;
        await context.CloseAsync();
        await video.SaveAsAsync(Path.Combine(media, "payflow-demo.webm"));
        Directory.Delete(videoDir, recursive: true);
    }

    private static BrowserNewContextOptions Options(ViewportSize viewport) => new()
    {
        BaseURL = E2E.BaseUrl,
        Locale = "en-US",
        TimezoneId = "Europe/Istanbul",
        ViewportSize = viewport,
        ColorScheme = ColorScheme.Light,
    };

    private static async Task SignInAsync(IPage page, bool typing)
    {
        await page.GotoAsync("/login");
        if (typing)
        {
            await page.GetByLabel("Email").PressSequentiallyAsync(DemoEmail, new() { Delay = 50 });
            await page.GetByLabel("Password").PressSequentiallyAsync(DemoPassword, new() { Delay = 50 });
        }
        else
        {
            await page.GetByLabel("Email").FillAsync(DemoEmail);
            await page.GetByLabel("Password").FillAsync(DemoPassword);
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();

        // With one company the dashboard opens directly; with several, the company chooser comes first.
        var dashboard = page.GetByRole(AriaRole.Heading, new() { Name = "Dashboard" });
        var chooser = page.GetByRole(AriaRole.Heading, new() { Name = "Your companies" });
        await Assertions.Expect(dashboard.Or(chooser)).ToBeVisibleAsync();
        if (await chooser.IsVisibleAsync())
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Demo Mağaza" }).ClickAsync();
        }

        await Assertions.Expect(dashboard).ToBeVisibleAsync();
    }

    private static async Task Settle(IPage page)
    {
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5_000 });
        }
        catch (TimeoutException)
        {
        }

        await page.WaitForTimeoutAsync(1_200); // chart entry animations
    }

    private static Task Pause(IPage page, int milliseconds = 1_200) => page.WaitForTimeoutAsync(milliseconds);
}
