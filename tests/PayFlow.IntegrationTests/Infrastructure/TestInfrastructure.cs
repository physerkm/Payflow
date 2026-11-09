using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PayFlow.Api;
using PayFlow.Application.Abstractions;
using PayFlow.Infrastructure.Messaging;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Worker;
using Testcontainers.PostgreSql;

namespace PayFlow.IntegrationTests.Infrastructure;

/// <summary>
/// One PostgreSQL server per test run: a Testcontainers container, or an existing server given by the
/// PAYFLOW_TEST_POSTGRES admin connection string (used where Docker is not available). Every fixture gets its
/// own freshly migrated database. The EF Core InMemory provider is deliberately never used.
/// </summary>
public static class PostgresServer
{
    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static PostgreSqlContainer? _container;
    private static string? _adminConnectionString;
    private static string? _unavailableReason;
    private static bool _initialized;

    public static async Task<(string? ConnectionString, string? Reason)> CreateDatabaseAsync()
    {
        await EnsureStartedAsync();
        if (_adminConnectionString is null)
        {
            return (null, _unavailableReason);
        }

        var name = "payflow_test_" + Guid.NewGuid().ToString("N")[..12];
        await using (var connection = new NpgsqlConnection(_adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await command.ExecuteNonQueryAsync();
        }

        return (new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = name, Pooling = true }.ConnectionString, null);
    }

    public static async Task DropDatabaseAsync(string connectionString)
    {
        if (_adminConnectionString is null)
        {
            return;
        }

        var name = new NpgsqlConnectionStringBuilder(connectionString).Database;
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    public static string? AdminConnectionString => _adminConnectionString;

    private static async Task EnsureStartedAsync()
    {
        await Lock.WaitAsync();
        try
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            var external = Environment.GetEnvironmentVariable("PAYFLOW_TEST_POSTGRES");
            if (!string.IsNullOrWhiteSpace(external))
            {
                _adminConnectionString = external;
                return;
            }

            try
            {
                _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
                await _container.StartAsync();
                _adminConnectionString = _container.GetConnectionString();
            }
            catch (Exception ex)
            {
                _unavailableReason = $"PostgreSQL is not available (no Docker and PAYFLOW_TEST_POSTGRES not set): {ex.GetType().Name}";
            }
        }
        finally
        {
            Lock.Release();
        }
    }
}

/// <summary>API host + Worker components (outbox publisher, in-memory transport, consumers, reconciliation) in one process.</summary>
public sealed class PayFlowApiFactory(
    string connectionString,
    IReadOnlyDictionary<string, string?>? overrides = null,
    Action<IServiceCollection>? configureServices = null) : WebApplicationFactory<ApiAssemblyMarker>
{
    public const string WebhookSecret = "whsec_integration_tests";

    public string ConnectionString { get; } = connectionString;

    public Dictionary<string, string?> Settings
    {
        get
        {
            var settings = BaseSettings;
            foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
            {
                settings[key] = value;
            }

            return settings;
        }
    }

    private Dictionary<string, string?> BaseSettings => new()
    {
        ["ConnectionStrings:PayFlow"] = ConnectionString,
        ["Database:MigrateOnStartup"] = "true",
        ["Auth:Jwt:SigningKey"] = "integration-tests-signing-key-0123456789abcdef",
        ["Auth:RefreshToken:SecureCookie"] = "false",
        ["MockProvider:WebhookSecret"] = WebhookSecret,
        ["MockProvider:SimulatedLatency"] = "00:00:00",
        ["MockProvider:EnableWebhookSimulator"] = "false",
        ["RateLimiting:Enabled"] = "false",
        ["Payments:RetryBaseDelay"] = "00:00:00.100",
        ["Payments:RetryMaxDelay"] = "00:00:00.500",
        ["Payments:ReconcileUnknownAfter"] = "00:00:00.500",
        ["Payments:ReconcileProviderPendingAfter"] = "00:10:00",
        ["Payments:ReconcileProcessingAfter"] = "00:10:00",
        ["Payments:Resilience:MinimumThroughput"] = "10000",
        ["Messaging:Transport"] = "InMemory",
        ["Messaging:OutboxPollInterval"] = "00:00:00.100",
        ["Messaging:MaxDeliveryRetries"] = "2",
        ["Worker:ReconciliationInterval"] = "00:00:00.300",
        ["Logging:LogLevel:Default"] = "Warning",
        ["Logging:LogLevel:PayFlow"] = "Warning",
    };

    public InMemoryMessageBus Bus => Services.GetRequiredService<InMemoryMessageBus>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Settings));
        builder.ConfigureTestServices(services =>
        {
            services.AddPayFlowWorker(new ConfigurationBuilder().AddInMemoryCollection(Settings).Build(), transportOverride: "InMemory");
            configureServices?.Invoke(services);
        });
    }

    /// <summary>Runs code against the database in a system scope for the given tenant (or none).</summary>
    public async Task<T> WithDbAsync<T>(Func<PayFlowDbContext, Task<T>> work, Guid? tenantId = null)
    {
        await using var scope = Services.CreateAsyncScope();
        if (tenantId is { } id)
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetSystem(id);
        }

        return await work(scope.ServiceProvider.GetRequiredService<PayFlowDbContext>());
    }
}

/// <summary>Class fixture: a dedicated database and host per test class.</summary>
public class ApiFixture : IAsyncLifetime
{
    private PayFlowApiFactory? _factory;

    /// <summary>Extra configuration for the host, given the freshly created database's connection string.</summary>
    protected virtual Task<IReadOnlyDictionary<string, string?>?> PrepareAsync(string connectionString) =>
        Task.FromResult<IReadOnlyDictionary<string, string?>?>(null);

    /// <summary>Service replacements for the host (applied after the application registrations).</summary>
    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    public PayFlowApiFactory Factory => _factory ?? throw new InvalidOperationException("Fixture not available.");

    public string? UnavailableReason { get; private set; }

    public bool IsAvailable => _factory is not null;

    public async ValueTask InitializeAsync()
    {
        var (connectionString, reason) = await PostgresServer.CreateDatabaseAsync();
        if (connectionString is null)
        {
            UnavailableReason = reason;
            return;
        }

        _factory = new PayFlowApiFactory(connectionString, await PrepareAsync(connectionString), ConfigureServices);
        _ = _factory.Server; // start the host: runs migrations and background services
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
            await PostgresServer.DropDatabaseAsync(_factory.ConnectionString);
        }
    }
}

public abstract class IntegrationTest(ApiFixture fixture) : IntegrationTest<ApiFixture>(fixture);

public abstract class IntegrationTest<TFixture> : IClassFixture<TFixture>
    where TFixture : ApiFixture
{
    protected IntegrationTest(TFixture fixture)
    {
        Assert.SkipUnless(fixture.IsAvailable, fixture.UnavailableReason ?? "PostgreSQL unavailable");
        Fixture = fixture;
    }

    protected ApiFixture Fixture { get; }

    protected PayFlowApiFactory Factory => Fixture.Factory;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected Task<TestSession> NewUserAsync(string? email = null) => TestSession.RegisterAsync(Factory, email);

    /// <summary>Registers a user, creates a tenant (user = Owner) and activates it.</summary>
    protected async Task<TestSession> NewTenantOwnerAsync(string? slug = null)
    {
        var session = await NewUserAsync();
        await session.CreateAndActivateTenantAsync(slug);
        return session;
    }

    protected static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null, string? because = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Condition not met in time{(because is null ? string.Empty : ": " + because)}.");
    }
}

public static class TestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}

/// <summary>An authenticated API client for one user (cookie container keeps the refresh cookie).</summary>
public sealed class TestSession
{
    private TestSession(HttpClient http, string email, Guid userId)
    {
        Http = http;
        Email = email;
        UserId = userId;
    }

    public HttpClient Http { get; }

    public string Email { get; }

    public Guid UserId { get; }

    public Guid? TenantId { get; private set; }

    public const string Password = "Str0ngPassw0rd!";

    public static async Task<TestSession> RegisterAsync(PayFlowApiFactory factory, string? email = null)
    {
        email ??= $"user-{Guid.NewGuid():N}@payflow.test";
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var response = await http.PostAsJsonAsync("/api/v1/auth/register", new { email, password = Password, displayName = "Test User" });
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<JsonElement>();
        var result = new TestSession(http, email, session.GetProperty("user").GetProperty("id").GetGuid());
        result.UseToken(session.GetProperty("accessToken").GetString()!);
        return result;
    }

    public void UseToken(string token) => Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    public async Task<Guid> CreateAndActivateTenantAsync(string? slug = null, string currency = "TRY")
    {
        slug ??= "t-" + Guid.NewGuid().ToString("N")[..10];
        var tenant = await PostOkAsync("/api/v1/tenants", new { name = "Tenant " + slug, slug, defaultCurrency = currency, timeZone = "Europe/Istanbul" });
        var tenantId = tenant.GetProperty("id").GetGuid();
        await ActivateAsync(tenantId);
        return tenantId;
    }

    public async Task ActivateAsync(Guid tenantId)
    {
        var session = await PostOkAsync("/api/v1/auth/activate-tenant", new { tenantId });
        UseToken(session.GetProperty("accessToken").GetString()!);
        TenantId = tenantId;
    }

    public async Task<Guid> CreateCustomerAsync(string name = "Customer") =>
        (await PostOkAsync("/api/v1/customers", new { name, email = $"{Guid.NewGuid():N}@example.com" })).GetProperty("id").GetGuid();

    public async Task<Guid> CreateProductAsync(long priceMinor = 10_000, string currency = "TRY", string? sku = null) =>
        (await PostOkAsync("/api/v1/products", new { sku = sku ?? "SKU-" + Guid.NewGuid().ToString("N")[..8], name = "Product", priceMinor, currency })).GetProperty("id").GetGuid();

    public async Task<JsonElement> CreateOrderAsync(Guid customerId, params (Guid ProductId, int Quantity)[] items) =>
        await PostOkAsync("/api/v1/orders", new { customerId, items = items.Select(i => new { productId = i.ProductId, quantity = i.Quantity }) });

    /// <summary>Creates customer + product + order and returns the order id.</summary>
    public async Task<Guid> CreateSimpleOrderAsync(long priceMinor = 10_000, int quantity = 1, string currency = "TRY")
    {
        var customer = await CreateCustomerAsync();
        var product = await CreateProductAsync(priceMinor, currency);
        return (await CreateOrderAsync(customer, (product, quantity))).GetProperty("id").GetGuid();
    }

    public async Task<HttpResponseMessage> PayAsync(Guid orderId, string token, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{orderId}/payments")
        {
            Content = JsonContent.Create(new { paymentMethodToken = token }),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        return await Http.SendAsync(request);
    }

    public async Task<Guid> PayOkAsync(Guid orderId, string token = "pm_test_success")
    {
        var response = await PayAsync(orderId, token);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("paymentId").GetGuid();
    }

    public async Task<HttpResponseMessage> PostWithKeyAsync(string url, object body, string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await Http.SendAsync(request);
    }

    public async Task<JsonElement> GetJsonAsync(string url)
    {
        var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<string> PaymentStatusAsync(Guid paymentId) =>
        (await GetJsonAsync($"/api/v1/payments/{paymentId}")).GetProperty("status").GetString()!;

    public async Task<string> OrderStatusAsync(Guid orderId) =>
        (await GetJsonAsync($"/api/v1/orders/{orderId}")).GetProperty("status").GetString()!;

    public async Task<JsonElement> PostOkAsync(string url, object body)
    {
        var response = await Http.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"POST {url} returned {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}

public static class ProblemExtensions
{
    public static async Task<string?> ProblemCodeAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
