using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Audit;
using PayFlow.Application.Customers;
using PayFlow.Application.Identity;
using PayFlow.Application.Notifications;
using PayFlow.Application.Orders;
using PayFlow.Application.Payments;
using PayFlow.Application.Payments.Processing;
using PayFlow.Application.Products;
using PayFlow.Application.Reporting;
using PayFlow.Application.Tenants;

namespace PayFlow.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);

        // Request-scoped context
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<CorrelationContext>();
        services.AddScoped<ICorrelationContext>(sp => sp.GetRequiredService<CorrelationContext>());
        services.AddScoped<IAuditTrail, AuditTrail>();

        // Identity & tenants
        services.AddScoped<AuthService>();
        services.AddScoped<TenantAccessResolver>();
        services.AddScoped<TenantService>();

        // Customers & catalog (+ their public module APIs)
        services.AddScoped<CustomerService>();
        services.AddScoped<ICustomerDirectory, CustomerDirectory>();
        services.AddScoped<ProductService>();
        services.AddScoped<IProductCatalog, ProductCatalog>();

        // Orders (+ module API used by payments)
        services.AddScoped<OrderService>();
        services.AddScoped<OrderPaymentCoordinator>();
        services.AddScoped<IOrderPaymentCoordinator>(sp => sp.GetRequiredService<OrderPaymentCoordinator>());
        services.AddScoped<IOrderLookup>(sp => sp.GetRequiredService<OrderPaymentCoordinator>());

        // Payments & refunds
        services.AddOptions<PaymentProcessingOptions>(); // bound to configuration by the composition root
        services.AddScoped<PaymentService>();
        services.AddScoped<RefundService>();
        services.AddScoped<IOrderPaymentsPort, OrderPaymentsPort>();
        services.AddScoped<ProcessPaymentAttemptHandler>();
        services.AddScoped<ReconcilePaymentHandler>();
        services.AddScoped<ProviderWebhookHandler>();
        services.AddScoped<ProcessRefundHandler>();

        // Notifications, reporting, audit
        services.AddScoped<NotificationService>();
        services.AddScoped<NotificationHandler>();
        services.AddScoped<ReportService>();
        services.AddScoped<AuditQueries>();

        // Platform administration (tenant-level operations only, no business data)
        services.AddOptions<Platform.PlatformOptions>(); // bound by the composition root
        services.AddScoped<Platform.IPlatformRoles, Platform.PlatformRoles>();
        services.AddScoped<Platform.PlatformService>();

        // AI reporting assistant (read-only, allowlisted tools)
        services.AddOptions<Assistant.AssistantOptions>(); // bound to configuration by the composition root
        services.AddScoped<Assistant.ITenantSettings, Assistant.TenantSettings>();
        services.AddScoped<Assistant.AssistantTools>();
        services.AddScoped<Assistant.AssistantService>();

        return services;
    }
}
