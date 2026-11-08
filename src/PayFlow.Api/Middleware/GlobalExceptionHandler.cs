using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using PayFlow.Application.Abstractions;
using PayFlow.Domain.Common;

namespace PayFlow.Api.Middleware;

/// <summary>
/// Maps exceptions to RFC 7807 problem details with a stable <c>code</c>. Unexpected errors are logged with the
/// correlation id and returned without internal details.
/// </summary>
internal sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            httpContext.Response.StatusCode = 499; // client closed request
            return true;
        }

        var (status, code, title, detail, errors) = Map(exception);
        if (status >= 500 && exception is not Application.Assistant.AssistantUnavailableException)
        {
            logger.LogError(exception, "Unhandled exception while processing {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }
        else if (status >= 500)
        {
            // Expected unavailability (e.g. assistant not configured): no stack trace noise.
            logger.LogWarning("Request rejected with {Status} {Code}: {Detail}", status, code, detail);
        }
        else
        {
            logger.LogInformation("Request rejected with {Status} {Code}: {Detail}", status, code, detail);
        }

        var problem = errors is null ? new ProblemDetails() : new ValidationProblemDetails(errors.ToDictionary(e => e.Key, e => e.Value));
        problem.Status = status;
        problem.Title = title;
        problem.Detail = detail;
        problem.Type = $"https://payflow.dev/problems/{code.Replace('_', '-')}";
        problem.Extensions["code"] = code;

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    private static (int Status, string Code, string Title, string? Detail, IReadOnlyDictionary<string, string[]>? Errors) Map(Exception exception) => exception switch
    {
        RequestValidationException e => (400, "validation_failed", "Validation failed", e.Message, e.Errors),
        DomainException { Kind: DomainErrorKind.Conflict } e => (409, e.Code, "Conflict", e.Message, null),
        DomainException { Kind: DomainErrorKind.Forbidden } e => (403, e.Code, "Forbidden", e.Message, null),
        DomainException e => (400, e.Code, "Business rule violated", e.Message, null),
        NotFoundException e => (404, "not_found", "Not found", e.Message, null),
        ForbiddenException e => (403, e.Code, "Forbidden", e.Message, null),
        ConflictException e => (409, e.Code, "Conflict", e.Message, null),
        ConcurrencyConflictException e => (409, "concurrency_conflict", "Conflict", e.Message, null),
        UniqueConstraintViolationException => (409, "duplicate", "Conflict", "A resource with the same unique value already exists.", null),
        AuthenticationFailedException e => (401, e.Code, "Unauthorized", e.Message, null),
        Application.Assistant.AssistantUnavailableException e => (503, "assistant_unavailable", "Assistant unavailable", e.Message, null),
        BadHttpRequestException { StatusCode: 413 } => (413, "payload_too_large", "Payload too large", "The request body is too large.", null),
        BadHttpRequestException e => (400, "bad_request", "Bad request", e.Message, null),
        _ => (500, "internal_error", "An unexpected error occurred", "The error was logged. Quote the correlation id when contacting support.", null),
    };
}

internal static class ProblemDetailsSetup
{
    public static IServiceCollection AddPayFlowProblemDetails(this IServiceCollection services) =>
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var http = context.HttpContext;
            context.ProblemDetails.Instance ??= $"{http.Request.Method} {http.Request.Path}";
            context.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier;
            if (http.RequestServices.GetService<ICorrelationContext>() is { } correlation)
            {
                context.ProblemDetails.Extensions["correlationId"] = correlation.CorrelationId;
            }
        });
}
