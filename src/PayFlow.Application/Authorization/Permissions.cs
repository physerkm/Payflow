using System.Collections.Frozen;
using PayFlow.Domain.Tenants;

namespace PayFlow.Application.Authorization;

/// <summary>Fine-grained permissions checked by endpoint policies. Hiding a button in the UI is never a security control.</summary>
public static class Permissions
{
    public const string CustomersRead = "customers.read";
    public const string CustomersWrite = "customers.write";
    public const string ProductsRead = "products.read";
    public const string ProductsWrite = "products.write";
    public const string OrdersRead = "orders.read";
    public const string OrdersWrite = "orders.write";
    public const string PaymentsRead = "payments.read";
    public const string PaymentsWrite = "payments.write";
    public const string RefundsCreate = "refunds.create";
    public const string MembersManage = "members.manage";
    public const string ReportsRead = "reports.read";
    public const string TenantManage = "tenant.manage";
    public const string AuditRead = "audit.read";

    public static IReadOnlyList<string> All { get; } =
    [
        CustomersRead, CustomersWrite, ProductsRead, ProductsWrite, OrdersRead, OrdersWrite,
        PaymentsRead, PaymentsWrite, RefundsCreate, MembersManage, ReportsRead, TenantManage, AuditRead,
    ];
}

/// <summary>
/// Role → permission matrix (spec §2.2). Rows not covered by the matrix follow the role descriptions:
/// operators run day-to-day catalog/customer work, finance owns payments/refunds/reports.
/// </summary>
public static class RolePermissions
{
    private static readonly FrozenDictionary<TenantRole, FrozenSet<string>> Matrix = new Dictionary<TenantRole, FrozenSet<string>>
    {
        [TenantRole.Owner] = Permissions.All.ToFrozenSet(),
        [TenantRole.Admin] = Permissions.All.ToFrozenSet(),
        [TenantRole.Finance] = new[]
        {
            Permissions.CustomersRead, Permissions.CustomersWrite, Permissions.ProductsRead,
            Permissions.OrdersRead, Permissions.OrdersWrite, Permissions.PaymentsRead, Permissions.PaymentsWrite,
            Permissions.RefundsCreate, Permissions.ReportsRead,
        }.ToFrozenSet(),
        [TenantRole.Operator] = new[]
        {
            Permissions.CustomersRead, Permissions.CustomersWrite, Permissions.ProductsRead, Permissions.ProductsWrite,
            Permissions.OrdersRead, Permissions.OrdersWrite, Permissions.PaymentsRead, Permissions.PaymentsWrite,
        }.ToFrozenSet(),
        [TenantRole.ReadOnly] = new[]
        {
            Permissions.CustomersRead, Permissions.ProductsRead, Permissions.OrdersRead, Permissions.PaymentsRead,
            Permissions.ReportsRead,
        }.ToFrozenSet(),
    }.ToFrozenDictionary();

    public static FrozenSet<string> For(TenantRole role) => Matrix[role];
}
