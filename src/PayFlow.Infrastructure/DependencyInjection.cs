using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Payments;
using PayFlow.Application.Reporting;
using PayFlow.Infrastructure.Identity;
using PayFlow.Infrastructure.Messaging;
using PayFlow.Infrastructure.PaymentProviders;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Infrastructure.Reporting;

namespace PayFlow.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "PayFlow";
    public const string SystemConnectionStringName = "PayFlowSystem";
    public const string MigrationsConnectionStringName = "PayFlowMigrations";

    /// <summary>Persistence, identity, payment provider and query services shared by the API and the Worker.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
        services.Configure<MockProviderOptions>(configuration.GetSection(MockProviderOptions.SectionName));
        services.Configure<ProviderResilienceOptions>(configuration.GetSection(ProviderResilienceOptions.SectionName));
        services.Configure<PaymentProcessingOptions>(configuration.GetSection(PaymentProcessingOptions.SectionName));
        services.Configure<MessagingOptions>(configuration.GetSection(MessagingOptions.SectionName));

        // Data sources are built lazily from the final configuration (test hosts override connection strings).
        services.AddSingleton(sp => BuildDataSource(RequireConnectionString(sp.GetRequiredService<IConfiguration>(), ConnectionStringName)));
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var connectionString = config.GetConnectionString(SystemConnectionStringName);
            return new SystemDataSource(string.IsNullOrWhiteSpace(connectionString) ? sp.GetRequiredService<NpgsqlDataSource>() : BuildDataSource(connectionString));
        });

        services.AddScoped<TenantSessionInterceptor>();
        services.AddScoped<IIntegrationEventMapper, IntegrationEventMapper>();
        services.AddDbContext<PayFlowDbContext>((sp, options) =>
        {
            options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
                .UseSnakeCaseNamingConvention();
            if (sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.EnableRowLevelSecurity)
            {
                options.AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>());
            }
        });

        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<PayFlowDbContext>());
        services.AddScoped<ITenantsDb>(sp => sp.GetRequiredService<PayFlowDbContext>());
        services.AddScoped<ICustomersDb>(sp => sp.GetRequiredService<PayFlowDbContext>());
        services.AddScoped<ICatalogDb>(sp => sp.GetRequiredService<PayFlowDbContext>());
        services.AddScoped<IOrdersDb>(sp => sp.GetRequiredService<PayFlowDbContext>());
        services.AddScoped<IPaymentsDb>(sp => sp.GetRequiredService<PayFlowDbContext>());
        services.AddScoped<INotificationsDb>(sp => sp.GetRequiredService<PayFlowDbContext>());
        services.AddScoped<IAuditDb>(sp => sp.GetRequiredService<PayFlowDbContext>());

        services.AddScoped<IInbox, Inbox>();
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();
        services.AddScoped<IWebhookEventStore, WebhookEventStore>();
        services.AddSingleton<ISystemPaymentQueries, SystemPaymentQueries>();
        services.AddSingleton<IReportingQueries, ReportingQueries>();

        // Identity: ASP.NET Core Identity user store, password policy and lockout (brute-force protection).
        services.AddIdentityCore<AppUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 10;
                o.Password.RequireDigit = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireUppercase = true;
                o.Password.RequireNonAlphanumeric = false;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddEntityFrameworkStores<PayFlowDbContext>();
        services.AddScoped<IIdentityService, IdentityService>();
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();
        services.AddScoped<IRefreshTokenStore, RefreshTokenStore>();

        // Payment provider: mock adapter wrapped with timeout + circuit breaker (singleton: breaker state is shared).
        services.AddSingleton<MockPaymentProvider>();
        services.AddSingleton<IPaymentProvider>(sp => new ResilientPaymentProvider(
            sp.GetRequiredService<MockPaymentProvider>(),
            sp.GetRequiredService<IOptions<ProviderResilienceOptions>>().Value,
            sp.GetRequiredService<ILogger<ResilientPaymentProvider>>()));

        services.Configure<PayFlow.Application.Platform.PlatformOptions>(configuration.GetSection(PayFlow.Application.Platform.PlatformOptions.SectionName));

        // AI assistant model adapter (disabled when no API key is configured).
        services.Configure<PayFlow.Application.Assistant.AssistantOptions>(configuration.GetSection(PayFlow.Application.Assistant.AssistantOptions.SectionName));
        services.AddSingleton<PayFlow.Application.Assistant.IAssistantModel, Assistant.AnthropicAssistantModel>();

        services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);
        return services;
    }

    /// <summary>Outbox publishing transport and consumers (Worker only).</summary>
    public static IServiceCollection AddMessagingTransport(this IServiceCollection services, IConfiguration configuration, string? transportOverride = null)
    {
        var options = configuration.GetSection(MessagingOptions.SectionName).Get<MessagingOptions>() ?? new MessagingOptions();
        if (transportOverride is not null)
        {
            options.Transport = transportOverride;
        }

        services.AddScoped<OutboxProcessor>();
        services.AddSingleton<MockWebhookSimulator>();
        services.AddHttpClient(MockWebhookSimulator.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));

        if (options.UseRabbitMq)
        {
            services.AddSingleton<RabbitMqConnectionProvider>();
            services.AddSingleton<IMessageBus, RabbitMqMessageBus>();
            services.AddHostedService<RabbitMqConsumerService>();
            services.AddHealthChecks().AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: ["ready"]);
        }
        else
        {
            services.AddSingleton<InMemoryMessageBus>();
            services.AddSingleton<IMessageBus>(sp => sp.GetRequiredService<InMemoryMessageBus>());
            services.AddHostedService<InMemoryConsumerService>();
        }

        return services;
    }

    public static string RequireConnectionString(IConfiguration configuration, string name) =>
        configuration.GetConnectionString(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Connection string '{name}' is not configured.");

    private static NpgsqlDataSource BuildDataSource(string connectionString) => new NpgsqlDataSourceBuilder(connectionString).Build();
}
