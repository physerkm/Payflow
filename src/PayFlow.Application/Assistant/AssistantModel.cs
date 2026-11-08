using System.Text.Json;

namespace PayFlow.Application.Assistant;

public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    /// <summary>Anthropic API key. When empty the assistant is disabled (the rest of PayFlow works normally).</summary>
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>low | medium | high - reasoning effort; reporting questions rarely need more than medium.</summary>
    public string Effort { get; set; } = "medium";

    public int MaxToolRounds { get; set; } = 4;

    public int MaxQuestionLength { get; set; } = 1000;
}

/// <summary>A tool the model may call. The schema is JSON Schema (object with properties).</summary>
public sealed record AssistantToolSpec(string Name, string Description, IReadOnlyDictionary<string, JsonElement> Properties, IReadOnlyList<string> Required);

public sealed record AssistantToolCall(string Id, string Name, JsonElement Input);

public sealed record AssistantToolResult(string Content, bool IsError);

public sealed record AssistantModelResult(string Answer, bool Refused, long InputTokens, long OutputTokens, int ToolRounds);

/// <summary>
/// Port to the language model. The implementation runs the tool-use loop, but every tool call goes back through
/// <paramref name="executeTool"/>, where PayFlow - not the model - enforces the allowlist, input validation,
/// tenant scope and permissions.
/// </summary>
public interface IAssistantModel
{
    bool IsConfigured { get; }

    string ModelName { get; }

    Task<AssistantModelResult> RunAsync(
        string systemPrompt,
        string userMessage,
        IReadOnlyList<AssistantToolSpec> tools,
        Func<AssistantToolCall, CancellationToken, Task<AssistantToolResult>> executeTool,
        int maxToolRounds,
        CancellationToken cancellationToken);
}

/// <summary>The assistant is not configured or the model provider is unavailable (HTTP 503).</summary>
public sealed class AssistantUnavailableException : Exception
{
    public AssistantUnavailableException(string message)
        : base(message)
    {
    }

    public AssistantUnavailableException()
        : base("The AI assistant is not available.")
    {
    }

    public AssistantUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
