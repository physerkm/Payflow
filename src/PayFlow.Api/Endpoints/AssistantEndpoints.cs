using PayFlow.Api.Authorization;
using PayFlow.Api.Extensions;
using PayFlow.Application.Assistant;
using PayFlow.Application.Authorization;

namespace PayFlow.Api.Endpoints;

internal static class AssistantEndpoints
{
    public const string AssistantRateLimitPolicy = "assistant";

    public static RouteGroupBuilder MapAssistantEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/assistant").WithTags("AI assistant");

        group.MapGet("/status", (AssistantService assistant) => TypedResults.Ok(assistant.GetStatus()))
            .RequireTenant()
            .WithSummary("Whether the read-only reporting assistant is configured, and its allowlisted tools");

        group.MapPost("/ask", async (AskAssistantRequest request, AssistantService assistant, CancellationToken ct) =>
                TypedResults.Ok(await assistant.AskAsync(request, ct)))
            .Validate<AskAssistantRequest>()
            .RequirePermission(Permissions.ReportsRead)
            .RequireRateLimiting(AssistantRateLimitPolicy)
            .WithSummary("Ask a natural-language question about payments, refunds and revenue (read-only, tenant-scoped)");

        return api;
    }
}
