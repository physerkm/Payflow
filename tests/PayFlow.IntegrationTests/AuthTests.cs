using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PayFlow.IntegrationTests.Infrastructure;

namespace PayFlow.IntegrationTests;

public sealed class AuthTests(ApiFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Register_returns_access_token_and_sets_httponly_refresh_cookie_only()
    {
        var http = Factory.CreateClient();
        var response = await http.PostAsJsonAsync("/api/v1/auth/register", new { email = $"{Guid.NewGuid():N}@payflow.test", password = TestSession.Password, displayName = "Ada" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldContain("accessToken");
        body.ShouldNotContain("refresh", Case.Insensitive); // refresh token never reaches JavaScript
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("payflow_refresh=", StringComparison.Ordinal));
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("samesite=strict", Case.Insensitive);
        cookie.ShouldContain("path=/api/v1/auth", Case.Insensitive);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_get_the_same_generic_401()
    {
        var user = await NewUserAsync();
        var http = Factory.CreateClient();

        var wrongPassword = await http.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = "WrongPassw0rd" }, Ct);
        var unknownUser = await http.PostAsJsonAsync("/api/v1/auth/login", new { email = "nobody@payflow.test", password = "WrongPassw0rd" }, Ct);

        wrongPassword.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknownUser.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await wrongPassword.ProblemCodeAsync()).ShouldBe("invalid_credentials");
        (await unknownUser.ProblemCodeAsync()).ShouldBe("invalid_credentials");
    }

    [Fact]
    public async Task Account_is_locked_after_repeated_failures_even_for_the_right_password()
    {
        var user = await NewUserAsync();
        var http = Factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            await http.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = "WrongPassw0rd" }, Ct);
        }

        var response = await http.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = TestSession.Password }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_reuse_of_an_old_token_revokes_the_family()
    {
        var user = await NewUserAsync();

        // Capture the first refresh cookie, then rotate it.
        var first = await user.Http.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = TestSession.Password }, Ct);
        var oldCookie = first.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("payflow_refresh=", StringComparison.Ordinal)).Split(';')[0];

        var rotated = await user.Http.PostAsJsonAsync("/api/v1/auth/refresh", new { }, Ct);
        rotated.StatusCode.ShouldBe(HttpStatusCode.OK);

        // An attacker replays the old token.
        var attacker = Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var replay = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh") { Content = JsonContent.Create(new { }) };
        replay.Headers.Add("Cookie", oldCookie);
        var replayResponse = await attacker.SendAsync(replay, Ct);
        replayResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await replayResponse.ProblemCodeAsync()).ShouldBe("refresh_token_reused");

        // The legitimate (rotated) session is revoked as well.
        var legit = await user.Http.PostAsJsonAsync("/api/v1/auth/refresh", new { }, Ct);
        legit.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var user = await NewUserAsync();
        (await user.Http.PostAsync("/api/v1/auth/logout", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await user.Http.PostAsJsonAsync("/api/v1/auth/refresh", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_keeps_the_active_tenant_only_when_membership_is_valid()
    {
        var owner = await NewTenantOwnerAsync();
        var response = await owner.Http.PostAsJsonAsync("/api/v1/auth/refresh", new { tenantId = owner.TenantId }, Ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("tenant").GetProperty("tenantId").GetGuid().ShouldBe(owner.TenantId!.Value);

        var stranger = await NewUserAsync();
        var foreign = await stranger.Http.PostAsJsonAsync("/api/v1/auth/refresh", new { tenantId = owner.TenantId }, Ct);
        (await foreign.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("tenant").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Weak_passwords_are_rejected_with_field_errors()
    {
        var http = Factory.CreateClient();
        var response = await http.PostAsJsonAsync("/api/v1/auth/register", new { email = "weak@payflow.test", password = "short", displayName = "W" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("password");
    }

    [Fact]
    public async Task Protected_endpoints_require_a_token()
    {
        var response = await Factory.CreateClient().GetAsync("/api/v1/orders", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.ProblemCodeAsync()).ShouldBe("authentication_required");
    }
}
