using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PayFlow.IntegrationTests.Infrastructure;

namespace PayFlow.IntegrationTests;

/// <summary>Cross-tenant access must be impossible (zero tolerance) and indistinguishable from "not found".</summary>
public sealed class TenantIsolationTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Tenant_B_cannot_read_or_modify_tenant_A_resources()
    {
        var a = await NewTenantOwnerAsync();
        var b = await NewTenantOwnerAsync();

        var customerA = await a.CreateCustomerAsync("A's customer");
        var productA = await a.CreateProductAsync();
        var orderA = (await a.CreateOrderAsync(customerA, (productA, 1))).GetProperty("id").GetGuid();
        var paymentA = await a.PayOkAsync(orderA);

        (await b.Http.GetAsync($"/api/v1/customers/{customerA}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Http.GetAsync($"/api/v1/products/{productA}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Http.GetAsync($"/api/v1/orders/{orderA}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Http.GetAsync($"/api/v1/payments/{paymentA}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Http.GetAsync($"/api/v1/payments/{paymentA}/attempts", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Http.PutAsJsonAsync($"/api/v1/customers/{customerA}", new { name = "hijacked" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Http.PatchAsync($"/api/v1/orders/{orderA}/cancel", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.PostWithKeyAsync($"/api/v1/payments/{paymentA}/refunds", new { amountMinor = 100 }, "x")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.PayAsync(orderA, "pm_test_success")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var listB = await b.GetJsonAsync("/api/v1/customers");
        listB.GetProperty("totalCount").GetInt64().ShouldBe(0);
        (await a.GetJsonAsync($"/api/v1/customers/{customerA}")).GetProperty("name").GetString().ShouldBe("A's customer");
    }

    [Fact]
    public async Task Referencing_another_tenants_customer_or_product_in_a_body_is_rejected()
    {
        var a = await NewTenantOwnerAsync();
        var b = await NewTenantOwnerAsync();
        var customerA = await a.CreateCustomerAsync();
        var productA = await a.CreateProductAsync();
        var customerB = await b.CreateCustomerAsync();

        var withForeignCustomer = await b.Http.PostAsJsonAsync("/api/v1/orders", new { customerId = customerA, items = new[] { new { productId = productA, quantity = 1 } } }, Ct);
        var withForeignProduct = await b.Http.PostAsJsonAsync("/api/v1/orders", new { customerId = customerB, items = new[] { new { productId = productA, quantity = 1 } } }, Ct);

        withForeignCustomer.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        withForeignProduct.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Client_supplied_tenant_headers_and_body_fields_are_ignored()
    {
        var a = await NewTenantOwnerAsync();
        var b = await NewTenantOwnerAsync();
        await a.CreateCustomerAsync("secret");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/customers");
        request.Headers.Add("X-Tenant-Id", a.TenantId.ToString());
        var response = await b.Http.SendAsync(request, Ct);

        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("totalCount").GetInt64().ShouldBe(0);

        var created = await b.PostOkAsync("/api/v1/customers", new { name = "mine", tenantId = a.TenantId });
        (await a.Http.GetAsync($"/api/v1/customers/{created.GetProperty("id").GetGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Activating_a_tenant_without_membership_is_forbidden()
    {
        var a = await NewTenantOwnerAsync();
        var stranger = await NewUserAsync();
        var response = await stranger.Http.PostAsJsonAsync("/api/v1/auth/activate-tenant", new { tenantId = a.TenantId }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Tenant_scoped_endpoints_require_an_active_tenant()
    {
        var user = await NewUserAsync();
        var response = await user.Http.GetAsync("/api/v1/customers", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ProblemCodeAsync()).ShouldBe("tenant_required");
    }
}

public sealed class AuthorizationTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    private async Task<(TestSession Owner, TestSession Member, Guid MembershipId)> InviteAsync(string role)
    {
        var owner = await NewTenantOwnerAsync();
        var member = await NewUserAsync();
        var invitation = await owner.PostOkAsync("/api/v1/tenants/current/invitations", new { email = member.Email, role });
        await member.PostOkAsync($"/api/v1/auth/invitations/{invitation.GetProperty("id").GetGuid()}/accept", new { });
        await member.ActivateAsync(owner.TenantId!.Value);

        var members = await owner.GetJsonAsync("/api/v1/tenants/current/members");
        var membershipId = members.EnumerateArray().Single(m => m.GetProperty("userId").GetGuid() == member.UserId).GetProperty("membershipId").GetGuid();
        return (owner, member, membershipId);
    }

    [Fact]
    public async Task Read_only_member_can_read_but_not_create_orders()
    {
        var (owner, readOnly, _) = await InviteAsync("ReadOnly");
        var customer = await owner.CreateCustomerAsync();
        var product = await owner.CreateProductAsync();

        (await readOnly.Http.GetAsync("/api/v1/orders", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var create = await readOnly.Http.PostAsJsonAsync("/api/v1/orders", new { customerId = customer, items = new[] { new { productId = product, quantity = 1 } } }, Ct);
        create.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await create.ProblemCodeAsync()).ShouldBe("permission_denied");
    }

    [Fact]
    public async Task Operator_cannot_refund_and_finance_cannot_manage_members()
    {
        var (owner, op, _) = await InviteAsync("Operator");
        var order = await owner.CreateSimpleOrderAsync();
        var payment = await owner.PayOkAsync(order);
        await WaitUntilAsync(async () => await owner.PaymentStatusAsync(payment) == "Succeeded");

        (await op.PostWithKeyAsync($"/api/v1/payments/{payment}/refunds", new { amountMinor = 100 }, "r")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var (_, finance, _) = await InviteAsync("Finance");
        (await finance.Http.GetAsync("/api/v1/tenants/current/members", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await finance.Http.GetAsync("/api/v1/reports/overview", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Revoked_membership_loses_access_on_the_next_request_without_new_login()
    {
        var (owner, member, membershipId) = await InviteAsync("Operator");
        (await member.Http.GetAsync("/api/v1/orders", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await owner.Http.PatchAsJsonAsync($"/api/v1/tenants/current/members/{membershipId}", new { status = "Disabled" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await member.Http.GetAsync("/api/v1/orders", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ProblemCodeAsync()).ShouldBe("tenant_access_revoked");
    }

    [Fact]
    public async Task Role_downgrade_takes_effect_immediately()
    {
        var (owner, member, membershipId) = await InviteAsync("Admin");
        (await member.Http.GetAsync("/api/v1/tenants/current/members", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await owner.Http.PatchAsJsonAsync($"/api/v1/tenants/current/members/{membershipId}", new { role = "ReadOnly" }, Ct);

        (await member.Http.GetAsync("/api/v1/tenants/current/members", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_cannot_escalate_to_owner_and_last_owner_cannot_be_demoted()
    {
        var (owner, admin, adminMembership) = await InviteAsync("Admin");
        (await admin.Http.PatchAsJsonAsync($"/api/v1/tenants/current/members/{adminMembership}", new { role = "Owner" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var members = await owner.GetJsonAsync("/api/v1/tenants/current/members");
        var ownerMembership = members.EnumerateArray().Single(m => m.GetProperty("userId").GetGuid() == owner.UserId).GetProperty("membershipId").GetGuid();
        var demote = await owner.Http.PatchAsJsonAsync($"/api/v1/tenants/current/members/{ownerMembership}", new { role = "Admin" }, Ct);
        demote.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await demote.ProblemCodeAsync()).ShouldBe("membership_last_owner");

        (await owner.Http.DeleteAsync($"/api/v1/tenants/current/members/{ownerMembership}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Invitations_cannot_be_accepted_by_other_users()
    {
        var owner = await NewTenantOwnerAsync();
        var invitation = await owner.PostOkAsync("/api/v1/tenants/current/invitations", new { email = "invitee@payflow.test", role = "Operator" });
        var other = await NewUserAsync();
        var response = await other.Http.PostAsJsonAsync($"/api/v1/auth/invitations/{invitation.GetProperty("id").GetGuid()}/accept", new { }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
