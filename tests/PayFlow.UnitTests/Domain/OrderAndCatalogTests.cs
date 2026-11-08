using PayFlow.Domain.Common;
using PayFlow.Domain.Customers;
using PayFlow.Domain.Orders;
using PayFlow.Domain.Products;
using PayFlow.Domain.Tenants;

namespace PayFlow.UnitTests.Domain;

public sealed class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static OrderLine Line(long price, int qty, string currency = "TRY") =>
        new(Guid.NewGuid(), "SKU-" + Guid.NewGuid().ToString("N")[..6], "Item", price, currency, qty);

    private static Order NewOrder(params OrderLine[] lines) =>
        Order.Create(Guid.NewGuid(), "SO-000001", Guid.NewGuid(), lines, null, Now);

    [Fact]
    public void Total_is_computed_from_item_snapshots()
    {
        var order = NewOrder(Line(150075, 2), Line(1000, 3));

        order.TotalMinor.ShouldBe(150075 * 2 + 1000 * 3);
        order.Items.Sum(i => i.LineTotalMinor).ShouldBe(order.TotalMinor);
        order.Status.ShouldBe(OrderStatus.Draft);
        order.Currency.ShouldBe("TRY");
    }

    [Fact]
    public void Items_with_different_currencies_are_rejected()
    {
        Should.Throw<DomainException>(() => NewOrder(Line(100, 1, "TRY"), Line(100, 1, "USD"))).Code.ShouldBe("order_currency_mismatch");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(Order.MaxQuantity + 1)]
    public void Invalid_quantities_are_rejected(int quantity)
    {
        Should.Throw<DomainException>(() => NewOrder(Line(100, quantity))).Code.ShouldBe("order_quantity_invalid");
    }

    [Fact]
    public void Duplicate_products_and_empty_orders_are_rejected()
    {
        var line = Line(100, 1);
        Should.Throw<DomainException>(() => NewOrder(line, line)).Code.ShouldBe("order_duplicate_product");
        Should.Throw<DomainException>(() => NewOrder()).Code.ShouldBe("order_lines_required");
    }

    [Fact]
    public void Line_total_overflow_is_detected_instead_of_wrapping()
    {
        Should.Throw<OverflowException>(() => NewOrder(Line(long.MaxValue / 2, 3)));
    }

    [Fact]
    public void Payment_lifecycle_transitions()
    {
        var order = NewOrder(Line(100, 1));
        order.MarkPaymentPending(Now);
        order.Status.ShouldBe(OrderStatus.PendingPayment);
        order.MarkPaymentFailed(Now);
        order.Status.ShouldBe(OrderStatus.PaymentFailed);
        order.MarkPaymentPending(Now);
        order.MarkPaid(Now);
        order.Status.ShouldBe(OrderStatus.Paid);
        order.PaidAt.ShouldBe(Now);

        Should.NotThrow(() => order.MarkPaid(Now)); // duplicate confirmation is harmless
    }

    [Fact]
    public void Paid_orders_cannot_be_cancelled_and_cancelled_orders_are_terminal()
    {
        var paid = NewOrder(Line(100, 1));
        paid.MarkPaymentPending(Now);
        paid.MarkPaid(Now);
        Should.Throw<DomainException>(() => paid.Cancel(Now)).Code.ShouldBe("order_invalid_transition");

        var cancelled = NewOrder(Line(100, 1));
        cancelled.Cancel(Now);
        Should.Throw<DomainException>(() => cancelled.MarkPaymentPending(Now));
        Should.Throw<DomainException>(() => cancelled.Cancel(Now));
    }

    [Fact]
    public void Draft_order_cannot_be_marked_paid_directly()
    {
        var order = NewOrder(Line(100, 1));
        Should.Throw<DomainException>(() => order.MarkPaid(Now)).Kind.ShouldBe(DomainErrorKind.Conflict);
    }
}

public sealed class MoneyTests
{
    [Theory]
    [InlineData("try", "TRY", 2)]
    [InlineData("JPY", "JPY", 0)]
    [InlineData("KWD", "KWD", 3)]
    public void Currencies_are_normalized_with_iso_minor_units(string input, string expected, int minorUnits)
    {
        CurrencyCatalog.Normalize(input).ShouldBe(expected);
        CurrencyCatalog.GetMinorUnits(expected).ShouldBe(minorUnits);
    }

    [Fact]
    public void Unknown_currency_is_rejected()
    {
        Should.Throw<DomainException>(() => new Money(100, "XXX")).Code.ShouldBe("currency_unsupported");
    }

    [Fact]
    public void Different_currencies_are_never_added()
    {
        Should.Throw<DomainException>(() => new Money(100, "TRY").Add(new Money(100, "USD"))).Code.ShouldBe("currency_mismatch");
    }

    [Fact]
    public void Arithmetic_is_exact_in_minor_units()
    {
        new Money(150075, "TRY").Multiply(3).AmountMinor.ShouldBe(450225);
        new Money(10, "TRY").Subtract(new Money(3, "TRY")).AmountMinor.ShouldBe(7);
    }
}

public sealed class CatalogAndTenantTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Theory]
    [InlineData("acme")]
    [InlineData("acme-2026")]
    public void Valid_slugs_are_accepted(string slug) => Tenant.NormalizeSlug(slug).ShouldBe(slug);

    [Theory]
    [InlineData("a")]
    [InlineData("-acme")]
    [InlineData("acme_ltd")]
    [InlineData("Acme Ltd")]
    public void Invalid_slugs_are_rejected(string slug) =>
        Should.Throw<DomainException>(() => Tenant.NormalizeSlug(slug)).Code.ShouldBe("tenant_slug_invalid");

    [Fact]
    public void Product_price_must_be_positive_and_sku_is_normalized()
    {
        var product = Product.Create(Guid.NewGuid(), " sku-001 ", "Plan", null, 1000, "try", Now);
        product.Sku.ShouldBe("SKU-001");
        product.Currency.ShouldBe("TRY");

        Should.Throw<DomainException>(() => product.Update("Plan", null, 0, "TRY", Now)).Code.ShouldBe("product_price_invalid");
    }

    [Fact]
    public void Customer_email_is_lowercased_and_deactivation_is_soft()
    {
        var customer = Customer.Create(Guid.NewGuid(), "Mehmet", "Mehmet@Example.COM", null, Now);
        customer.Email.ShouldBe("mehmet@example.com");
        customer.Deactivate(Now);
        customer.Status.ShouldBe(CustomerStatus.Inactive);
    }
}

public sealed class MembershipPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Last_owner_cannot_be_demoted_disabled_or_removed()
    {
        var owner = TenantMembership.Create(Guid.NewGuid(), Guid.NewGuid(), TenantRole.Owner, Now);

        Should.Throw<DomainException>(() => MembershipPolicy.EnsureOwnerRemains(owner, 1, TenantRole.Admin, false)).Code.ShouldBe("membership_last_owner");
        Should.Throw<DomainException>(() => MembershipPolicy.EnsureOwnerRemains(owner, 1, null, true));
        Should.NotThrow(() => MembershipPolicy.EnsureOwnerRemains(owner, 2, TenantRole.Admin, false));
    }

    [Fact]
    public void Admins_cannot_grant_or_modify_the_owner_role()
    {
        var owner = TenantMembership.Create(Guid.NewGuid(), Guid.NewGuid(), TenantRole.Owner, Now);

        Should.Throw<DomainException>(() => MembershipPolicy.EnsureCanAssign(TenantRole.Admin, TenantRole.Owner)).Kind.ShouldBe(DomainErrorKind.Forbidden);
        Should.Throw<DomainException>(() => MembershipPolicy.EnsureCanModify(TenantRole.Admin, owner)).Code.ShouldBe("membership_owner_only");
        Should.NotThrow(() => MembershipPolicy.EnsureCanAssign(TenantRole.Owner, TenantRole.Owner));
        Should.NotThrow(() => MembershipPolicy.EnsureCanAssign(TenantRole.Admin, TenantRole.Operator));
    }

    [Theory]
    [InlineData(TenantRole.Finance)]
    [InlineData(TenantRole.Operator)]
    [InlineData(TenantRole.ReadOnly)]
    public void Non_admin_roles_cannot_manage_members(TenantRole role) =>
        Should.Throw<DomainException>(() => MembershipPolicy.EnsureCanAssign(role, TenantRole.ReadOnly)).Code.ShouldBe("membership_forbidden");

    [Fact]
    public void Invitation_can_only_be_accepted_by_the_invited_email_while_open()
    {
        var invitation = TenantInvitation.Create(Guid.NewGuid(), "new@acme.test", TenantRole.Operator, Guid.NewGuid(), Now);

        Should.Throw<DomainException>(() => invitation.Accept(Guid.NewGuid(), "OTHER@ACME.TEST", Now)).Code.ShouldBe("invitation_email_mismatch");
        Should.Throw<DomainException>(() => invitation.Accept(Guid.NewGuid(), "NEW@ACME.TEST", Now.AddDays(8))).Code.ShouldBe("invitation_not_open");

        invitation.Accept(Guid.NewGuid(), "NEW@ACME.TEST", Now);
        invitation.Status.ShouldBe(InvitationStatus.Accepted);
    }
}
