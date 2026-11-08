namespace PayFlow.Infrastructure.Observability;

/// <summary>
/// Container health check without curl/wget in the image: <c>dotnet PayFlow.Api.dll --healthcheck</c> calls the
/// local readiness endpoint and returns 0 (healthy) or 1.
/// </summary>
public static class HealthProbe
{
    public const string Argument = "--healthcheck";

    public static async Task<int> RunAsync(string path = "/health/ready")
    {
        var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';', ',')[0] ?? "8080";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}{path}"));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
