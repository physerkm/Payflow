using System.Reflection;
using NetArchTest.Rules;

namespace PayFlow.ArchitectureTests;

/// <summary>Enforces the project-reference rules of spec §4.4 / §6.1 and the module boundaries.</summary>
public sealed class LayerDependencyTests
{
    private static readonly Assembly Domain = typeof(PayFlow.Domain.Common.Entity).Assembly;
    private static readonly Assembly Application = typeof(PayFlow.Application.DependencyInjection).Assembly;
    private static readonly Assembly Infrastructure = typeof(PayFlow.Infrastructure.DependencyInjection).Assembly;
    private static readonly Assembly Contracts = typeof(PayFlow.Contracts.MessageEnvelope).Assembly;

    [Fact]
    public void Domain_has_no_framework_or_layer_dependencies()
    {
        var result = Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOnAny(
                "PayFlow.Application", "PayFlow.Infrastructure", "PayFlow.Api", "PayFlow.Worker", "PayFlow.Contracts",
                "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql", "RabbitMQ", "System.Net.Http")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Application_depends_only_on_domain_and_abstractions()
    {
        var result = Types.InAssembly(Application)
            .ShouldNot()
            .HaveDependencyOnAny(
                "PayFlow.Infrastructure", "PayFlow.Api", "PayFlow.Worker", "PayFlow.Contracts",
                "Microsoft.AspNetCore", "Npgsql", "RabbitMQ", "Microsoft.EntityFrameworkCore.Relational", "Polly")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Contracts_do_not_carry_domain_entities()
    {
        var result = Types.InAssembly(Contracts)
            .ShouldNot()
            .HaveDependencyOnAny("PayFlow.Domain", "PayFlow.Application", "PayFlow.Infrastructure")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_hosts()
    {
        var result = Types.InAssembly(Infrastructure)
            .ShouldNot()
            .HaveDependencyOnAny("PayFlow.Api", "PayFlow.Worker")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Theory]
    [InlineData("PayFlow.Domain.Orders", new[] { "PayFlow.Domain.Payments", "PayFlow.Domain.Refunds", "PayFlow.Domain.Customers", "PayFlow.Domain.Products" })]
    [InlineData("PayFlow.Domain.Payments", new[] { "PayFlow.Domain.Orders", "PayFlow.Domain.Refunds", "PayFlow.Domain.Customers" })]
    [InlineData("PayFlow.Domain.Customers", new[] { "PayFlow.Domain.Orders", "PayFlow.Domain.Payments" })]
    [InlineData("PayFlow.Domain.Products", new[] { "PayFlow.Domain.Orders", "PayFlow.Domain.Payments" })]
    public void Domain_modules_do_not_reference_each_other(string module, string[] forbidden)
    {
        var result = Types.InAssembly(Domain).That().ResideInNamespace(module).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Orders_module_never_depends_on_payments_module()
    {
        // Payments depends on Orders' public API (and implements the Orders port), never the other way round.
        var result = Types.InAssembly(Application)
            .That().ResideInNamespace("PayFlow.Application.Orders")
            .ShouldNot().HaveDependencyOnAny("PayFlow.Application.Payments", "PayFlow.Domain.Payments")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Payments_module_does_not_touch_order_tables_directly()
    {
        var result = Types.InAssembly(Application)
            .That().ResideInNamespace("PayFlow.Application.Payments")
            .ShouldNot().HaveDependencyOnAny("PayFlow.Application.Abstractions.IOrdersDb", "PayFlow.Domain.Orders.Order", "PayFlow.Application.Abstractions.ICustomersDb")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Catalog_and_customer_modules_are_independent_of_orders_and_payments()
    {
        var result = Types.InAssembly(Application)
            .That().ResideInNamespace("PayFlow.Application.Customers").Or().ResideInNamespace("PayFlow.Application.Products")
            .ShouldNot().HaveDependencyOnAny("PayFlow.Application.Orders", "PayFlow.Application.Payments", "PayFlow.Application.Abstractions.IOrdersDb", "PayFlow.Application.Abstractions.IPaymentsDb")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Domain_entities_expose_no_public_setters()
    {
        var offenders = Domain.GetTypes()
            .Where(t => t.IsClass && t.Namespace?.StartsWith("PayFlow.Domain", StringComparison.Ordinal) == true && !IsRecord(t))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.SetMethod?.IsPublic == true)
                .Select(p => $"{t.Name}.{p.Name}"))
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Exceptions_and_handlers_are_sealed()
    {
        var result = Types.InAssembly(Application)
            .That().AreClasses().And().HaveNameEndingWith("Handler").Or().AreClasses().And().HaveNameEndingWith("Service")
            .Should().BeSealed()
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    private static bool IsRecord(Type type) => type.GetMethod("<Clone>$") is not null;

    private static string Describe(NetArchTest.Rules.TestResult result) =>
        "Violations: " + string.Join(", ", result.FailingTypeNames ?? []);
}
