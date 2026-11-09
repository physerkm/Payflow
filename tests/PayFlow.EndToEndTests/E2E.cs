using Microsoft.Playwright;

namespace PayFlow.EndToEndTests;

/// <summary>Settings and helpers shared by the browser tests.</summary>
internal static class E2E
{
    public const string SkipReason = "Set PAYFLOW_E2E_BASE_URL to run browser tests against a running stack.";

    public static readonly string? BaseUrl = Environment.GetEnvironmentVariable("PAYFLOW_E2E_BASE_URL");

    public static bool Disabled => string.IsNullOrWhiteSpace(BaseUrl);

    public static Task<IBrowser> LaunchAsync(IPlaywright playwright) =>
        playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = Environment.GetEnvironmentVariable("PAYFLOW_E2E_BROWSER_CHANNEL"),
            Headless = Environment.GetEnvironmentVariable("PAYFLOW_E2E_HEADED") != "1",
        });

    /// <summary>Path for a screenshot or report kept as a CI artifact.</summary>
    public static string Artifact(string name)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "e2e-artifacts");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }
}
