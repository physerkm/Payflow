using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayFlow.Application.Assistant;
using PayFlow.Infrastructure.Observability;

namespace PayFlow.Infrastructure.Assistant;

/// <summary>
/// Claude adapter for the reporting assistant: a manual tool-use loop over the Messages API. Tool calls are handed
/// to the application's executor (allowlist + validation + tenant scope); this class only moves messages.
/// Server-side refusal fallback is enabled; a remaining refusal is reported as such, never as an answer.
/// </summary>
internal sealed class AnthropicAssistantModel : IAssistantModel
{
    private static readonly Counter<long> Requests = PayFlowMetrics.Meter.CreateCounter<long>("payflow.assistant.requests", description: "Assistant model calls by outcome");
    private static readonly Counter<long> Tokens = PayFlowMetrics.Meter.CreateCounter<long>("payflow.assistant.tokens", description: "Assistant tokens by direction");
    private static readonly Histogram<double> Latency = PayFlowMetrics.Meter.CreateHistogram<double>("payflow.assistant.latency", unit: "ms", description: "Assistant end-to-end model latency");

    private readonly AnthropicClient? _client;
    private readonly AssistantOptions _options;
    private readonly ILogger<AnthropicAssistantModel> _logger;

    public AnthropicAssistantModel(IOptions<AssistantOptions> options, ILogger<AnthropicAssistantModel> logger)
    {
        _options = options.Value;
        _logger = logger;
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _client = new AnthropicClient { ApiKey = _options.ApiKey };
        }
    }

    public bool IsConfigured => _client is not null;

    public string ModelName => _options.Model;

    public async Task<AssistantModelResult> RunAsync(
        string systemPrompt,
        string userMessage,
        IReadOnlyList<AssistantToolSpec> tools,
        Func<AssistantToolCall, CancellationToken, Task<AssistantToolResult>> executeTool,
        int maxToolRounds,
        CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            throw new AssistantUnavailableException("The AI assistant is not configured.");
        }

        using var activity = PayFlowTelemetry.ActivitySource.StartActivity("assistant run", ActivityKind.Client);
        activity?.SetTag("gen_ai.request.model", ModelName);
        var stopwatch = Stopwatch.StartNew();
        var toolDefinitions = tools.Select(ToToolUnion).ToList();
        List<BetaMessageParam> messages = [new BetaUserMessageParam { Content = userMessage }];
        long inputTokens = 0, outputTokens = 0;

        try
        {
            for (var round = 0; ; round++)
            {
                var response = await _client.Beta.Messages.Create(
                    new MessageCreateParams
                    {
                        Model = ModelName,
                        MaxTokens = 16000,
                        System = systemPrompt,
                        Tools = toolDefinitions,
                        Messages = messages,
                        Thinking = new BetaThinkingConfigAdaptive(),
                        OutputConfig = new BetaOutputConfig { Effort = ParseEffort(_options.Effort) },
                        Betas = ["server-side-fallback-2026-07-01"],
                        Fallbacks = new Default(), // fallbacks: "default" - Anthropic picks the fallback model per refusal category
                    },
                    cancellationToken: cancellationToken);

                inputTokens += response.Usage.InputTokens;
                outputTokens += response.Usage.OutputTokens;

                if (response.StopReason == "refusal")
                {
                    Record("refused", stopwatch, inputTokens, outputTokens);
                    return new AssistantModelResult("I can't help with that request. I can answer questions about your company's payments, refunds and revenue.", true, inputTokens, outputTokens, round);
                }

                var (assistantContent, toolCalls, text) = ReadResponse(response);
                if (response.StopReason != "tool_use" || toolCalls.Count == 0)
                {
                    Record("answered", stopwatch, inputTokens, outputTokens);
                    return new AssistantModelResult(text.Length > 0 ? text : "I could not produce an answer.", false, inputTokens, outputTokens, round);
                }

                if (round >= maxToolRounds)
                {
                    Record("tool_limit", stopwatch, inputTokens, outputTokens);
                    return new AssistantModelResult("This question needs more report lookups than I am allowed to make. Please narrow it down.", false, inputTokens, outputTokens, round);
                }

                // One tool_result per tool_use, all in a single user message.
                List<BetaContentBlockParam> results = [];
                foreach (var call in toolCalls)
                {
                    var output = await executeTool(call, cancellationToken);
                    results.Add(new BetaToolResultBlockParam { ToolUseID = call.Id, Content = output.Content, IsError = output.IsError });
                }

                messages.Add(new BetaAssistantMessageParam { Content = assistantContent });
                messages.Add(new BetaUserMessageParam { Content = results });
            }
        }
        catch (AnthropicRateLimitException ex)
        {
            Record("rate_limited", stopwatch, inputTokens, outputTokens);
            throw new AssistantUnavailableException("The AI assistant is busy. Please try again in a moment.", ex);
        }
        catch (AnthropicApiException ex)
        {
            Record("error", stopwatch, inputTokens, outputTokens);
            _logger.LogError(ex, "Assistant model call failed");
            throw new AssistantUnavailableException("The AI assistant is temporarily unavailable.", ex);
        }
    }

    /// <summary>Echoes the assistant turn back unchanged (thinking blocks with signatures, fallback markers, tool uses).</summary>
    private static (List<BetaContentBlockParam> Content, List<AssistantToolCall> Calls, string Text) ReadResponse(BetaMessage response)
    {
        List<BetaContentBlockParam> content = [];
        List<AssistantToolCall> calls = [];
        var text = new System.Text.StringBuilder();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var t))
            {
                content.Add(new BetaTextBlockParam { Text = t.Text });
                text.Append(t.Text);
            }
            else if (block.TryPickThinking(out var thinking))
            {
                content.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
            }
            else if (block.TryPickRedactedThinking(out var redacted))
            {
                content.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
            }
            else if (block.TryPickToolUse(out var toolUse))
            {
                content.Add(new BetaToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                calls.Add(new AssistantToolCall(toolUse.ID, toolUse.Name, JsonSerializer.SerializeToElement(toolUse.Input)));
            }
            else if (block.TryPickFallback(out var fallback))
            {
                content.Add(new BetaFallbackBlockParam { From = new() { Model = fallback.From.Model }, To = new() { Model = fallback.To.Model } });
            }
        }

        return (content, calls, text.ToString().Trim());
    }

    private static BetaToolUnion ToToolUnion(AssistantToolSpec spec) => new BetaTool
    {
        Name = spec.Name,
        Description = spec.Description,
        InputSchema = new()
        {
            Properties = spec.Properties.ToDictionary(p => p.Key, p => p.Value),
            Required = [.. spec.Required],
        },
    };

    private static Effort ParseEffort(string value) => value.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        _ => Effort.Medium,
    };

    private void Record(string outcome, Stopwatch stopwatch, long input, long output)
    {
        var model = new KeyValuePair<string, object?>("model", ModelName);
        Requests.Add(1, new KeyValuePair<string, object?>("outcome", outcome), model);
        Tokens.Add(input, new KeyValuePair<string, object?>("direction", "input"), model);
        Tokens.Add(output, new KeyValuePair<string, object?>("direction", "output"), model);
        Latency.Record(stopwatch.Elapsed.TotalMilliseconds, model);
    }
}
