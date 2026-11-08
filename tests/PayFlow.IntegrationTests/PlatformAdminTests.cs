using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PayFlow.IntegrationTests.Infrastructure;

namespace PayFlow.IntegrationTests;

public sealed class PlatformApiFixture : ApiFixture
{
    public const string AdminEmail = "platform-admin@payflow.test";
    public const string AgentEmail = "support-agent@payflow.test";

    protected override Task<IReadOnlyDictionary<string, string?>?> PrepareAsync(string connectionString) =>
        Task.FromResult<IReadOnlyDictionary<string, string?>?>(new Dictionary<string, string?>
        {
            ["Platform:Admins:0"] = AdminEmail,
            ["Platform:SupportAgents:0"] = AgentEmail,
        });
}

/// <summary>Platform staff manage tenants as a whole (spec §2.1) and never see tenant business data.</summary>
public sealed class PlatformAdminTests(PlatformApiFixture fixture) : IntegrationTest<PlatformApiFixture>(fixture)
{
    private static TestSession? _admin;
    private static TestSession? _agent;

    // Platform users are registered once per test run (their emails are configured on the fixture).
    private async Task<TestSession> AdminAsync() => _admin ??= await TestSession.RegisterAsync(Factory, PlatformApiFixture.AdminEmail);

    private async Task<TestSession> AgentAsync() => _agent ??= await TestSession.RegisterAsync(Factory, PlatformApiFixture.AgentEmail);

    [Fact]
    public async Task Regular_users_and_tenant_owners_have_no_platform_access()
    {
        var owner = await NewTenantOwnerAsync();
        var response = await owner.Http.GetAsync("/api/v1/platform/tenants", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ProblemCodeAsync()).ShouldBe("platform_access_denied");
        (await owner.GetJsonAsync("/api/v1/auth/me")).GetProperty("platformRole").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Suspending_a_tenant_blocks_its_members_immediately_and_reactivation_restores_access()
    {
        var admin = await AdminAsync();
        var owner = await NewTenantOwnerAsync();
        (await owner.Http.GetAsync("/api/v1/orders", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.GetJsonAsync("/api/v1/auth/me")).GetProperty("platformRole").GetString().ShouldBe("PlatformAdmin");

        var suspended = await admin.Http.PatchAsJsonAsync($"/api/v1/platform/tenants/{owner.TenantId}/status", new { status = "Suspended", reason = "Chargeback investigation" }, Ct);
        suspended.StatusCode.ShouldBe(HttpStatusCode.OK);

        var blocked = await owner.Http.GetAsync("/api/v1/orders", Ct);
        blocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await blocked.ProblemCodeAsync()).ShouldBe("tenant_access_revoked");
        (await owner.Http.PostAsJsonAsync("/api/v1/auth/activate-tenant", new { tenantId = owner.TenantId }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await admin.Http.PatchAsJsonAsync($"/api/v1/platform/tenants/{owner.TenantId}/status", new { status = "Active", reason = "Resolved" }, Ct);
        (await owner.Http.GetAsync("/api/v1/orders", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The change is audited in the tenant's own audit trail.
        var audit = await owner.GetJsonAsync("/api/v1/audit-logs?action=platform.tenant_status_changed");
        audit.GetProperty("totalCount").GetInt64().ShouldBe(2);
    }

    [Fact]
    public async Task Support_agents_can_list_tenants_but_not_change_them()
    {
        var agent = await AgentAsync();
        var owner = await NewTenantOwnerAsync();

        var list = await agent.GetJsonAsync("/api/v1/platform/tenants?pageSize=100");
        var row = list.GetProperty("items").EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == owner.TenantId);
        row.GetProperty("activeMembers").GetInt32().ShouldBe(1);

        var change = await agent.Http.PatchAsJsonAsync($"/api/v1/platform/tenants/{owner.TenantId}/status", new { status = "Suspended", reason = "test" }, Ct);
        change.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Platform_roles_do_not_grant_access_to_tenant_business_data()
    {
        var admin = await AdminAsync();
        var owner = await NewTenantOwnerAsync();
        var order = await owner.CreateSimpleOrderAsync();

        (await admin.Http.GetAsync($"/api/v1/orders/{order}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await admin.Http.PostAsJsonAsync("/api/v1/auth/activate-tenant", new { tenantId = owner.TenantId }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Status_changes_require_a_reason()
    {
        var admin = await AdminAsync();
        var owner = await NewTenantOwnerAsync();
        var response = await admin.Http.PatchAsJsonAsync($"/api/v1/platform/tenants/{owner.TenantId}/status", new { status = "Suspended", reason = "" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
