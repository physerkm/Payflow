using PayFlow.Application.Abstractions;
using PayFlow.Application.Authorization;
using PayFlow.Application.Notifications;
using PayFlow.Application.Payments;
using PayFlow.Domain.Tenants;

namespace PayFlow.UnitTests.Application;

public sealed class PermissionMatrixTests
{
    // Spec §2.2 role/permission matrix. Rows: operation; columns: Owner, Admin, Finance, Operator, ReadOnly.
    public static TheoryData<string, bool, bool, bool, bool, bool> Matrix => new()
    {
        { Permissions.OrdersRead, true, true, true, true, true },
        { Permissions.OrdersWrite, true, true, true, true, false },
        { Permissions.PaymentsRead, true, true, true, true, true },
        { Permissions.RefundsCreate, true, true, true, false, false },
        { Permissions.MembersManage, true, true, false, false, false },
        { Permissions.ReportsRead, true, true, true, false, true },
        { Permissions.TenantManage, true, true, false, false, false },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Role_permissions_follow_the_specification(string permission, bool owner, bool admin, bool finance, bool @operator, bool readOnly)
    {
        RolePermissions.For(TenantRole.Owner).Contains(permission).ShouldBe(owner);
        RolePermissions.For(TenantRole.Admin).Contains(permission).ShouldBe(admin);
        RolePermissions.For(TenantRole.Finance).Contains(permission).ShouldBe(finance);
        RolePermissions.For(TenantRole.Operator).Contains(permission).ShouldBe(@operator);
        RolePermissions.For(TenantRole.ReadOnly).Contains(permission).ShouldBe(readOnly);
    }

    [Fact]
    public void Read_only_role_has_no_write_permissions()
    {
        RolePermissions.For(TenantRole.ReadOnly).ShouldNotContain(p => p.EndsWith(".write", StringComparison.Ordinal) || p.EndsWith(".create", StringComparison.Ordinal) || p.EndsWith(".manage", StringComparison.Ordinal));
    }

    [Fact]
    public void Tenant_context_without_role_has_no_permissions()
    {
        ITenantContext context = new TenantContext();
        context.HasPermission(Permissions.OrdersRead).ShouldBeFalse();

        var system = new TenantContext();
        system.SetSystem(Guid.NewGuid());
        ((ITenantContext)system).HasPermission(Permissions.OrdersRead).ShouldBeFalse();
    }
}

public sealed class RetryBackoffTests
{
    [Theory]
    [InlineData(1, 1000, 2000)]
    [InlineData(2, 2000, 4000)]
    [InlineData(3, 4000, 8000)]
    public void Delay_grows_exponentially_with_bounded_jitter(int attempt, double minMs, double maxMs)
    {
        for (var i = 0; i < 50; i++)
        {
            var delay = RetryBackoff.Compute(attempt, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1));
            delay.TotalMilliseconds.ShouldBeInRange(minMs, maxMs);
        }
    }

    [Fact]
    public void Delay_never_exceeds_the_maximum()
    {
        RetryBackoff.Compute(30, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)).ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(10));
    }
}

public sealed class PagingAndSortingTests
{
    [Theory]
    [InlineData(0, 0, 1, PageRequest.DefaultPageSize)]
    [InlineData(-5, 1000, 1, PageRequest.MaxPageSize)]
    [InlineData(3, 50, 3, 50)]
    public void Page_requests_are_clamped(int page, int size, int expectedPage, int expectedSize)
    {
        var request = new PageRequest(page, size);
        request.NormalizedPage.ShouldBe(expectedPage);
        request.NormalizedPageSize.ShouldBe(expectedSize);
    }

    [Fact]
    public void Sort_is_restricted_to_an_allowlist()
    {
        var fallback = new SortSpec("createdAt", true);
        SortSpec.Parse("name:asc", ["name", "createdAt"], fallback).ShouldBe(new SortSpec("name", false));
        SortSpec.Parse("NAME:desc", ["name"], fallback).ShouldBe(new SortSpec("name", true));
        SortSpec.Parse(null, ["name"], fallback).ShouldBe(fallback);
        Should.Throw<RequestValidationException>(() => SortSpec.Parse("password_hash", ["name"], fallback));
        Should.Throw<RequestValidationException>(() => SortSpec.Parse("name:sideways", ["name"], fallback));
    }
}

public sealed class FormattingTests
{
    [Theory]
    [InlineData(150075, "TRY", "1,500.75 TRY")]
    [InlineData(1500, "JPY", "1,500 JPY")]
    [InlineData(1234, "KWD", "1.234 KWD")]
    public void Amounts_are_formatted_with_currency_minor_units(long minor, string currency, string expected) =>
        NotificationHandler.FormatAmount(minor, currency).ShouldBe(expected);
}
