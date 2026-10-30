using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace VeskaFlow.Web.Services;

public enum AiBlockKind { Text, ToolUse, ToolResult, Other }

/// <summary>
/// One piece of a message, in terms that belong to no vendor. <c>Native</c> carries a block the model must get back unchanged
/// (its reasoning), so the conversation loop can copy it without knowing what it is.
/// </summary>
public sealed record AiBlock(AiBlockKind Kind, string Text = "", string Id = "", string Name = "", string Json = "{}", bool IsError = false, object? Native = null)
{
    public static AiBlock OfText(string text) => new(AiBlockKind.Text, Text: text);
    public static AiBlock ToolResult(string toolUseId, string content, bool isError) => new(AiBlockKind.ToolResult, Text: content, Id: toolUseId, IsError: isError);
}

/// <summary><c>Role</c> is "user" or "assistant".</summary>
public sealed record AiMessage(string Role, IReadOnlyList<AiBlock> Blocks)
{
    public static AiMessage User(string text) => new("user", [AiBlock.OfText(text)]);
    public static AiMessage Assistant(string text) => new("assistant", [AiBlock.OfText(text)]);
}

/// <summary>A tool the model may call. <c>Properties</c> is a JSON-schema property map; all <c>Required</c> names must be in it.</summary>
public sealed record AiTool(string Name, string Description, IReadOnlyDictionary<string, object> Properties, IReadOnlyList<string> Required);

/// <summary>What the model answered in one step. <c>StopReason</c> is the vendor's word ("tool_use", "end_turn", "refusal", ...).</summary>
public sealed record AiStep(IReadOnlyList<AiBlock> Blocks, string StopReason, long InputTokens, long OutputTokens);

/// <summary>One step of a conversation with a language model. The only place that knows which vendor is behind it.</summary>
public interface IAiModel
{
    /// <summary>False until the model service has a key; the AI receptionist then stays out of sight.</summary>
    bool IsConfigured { get; }

    Task<AiStep> NextAsync(string system, IReadOnlyList<AiMessage> messages, IReadOnlyList<AiTool> tools, CancellationToken ct = default);
}

/// <summary>
/// Claude through the official Anthropic SDK. The key comes from <c>Anthropic:ApiKey</c> or the <c>ANTHROPIC_API_KEY</c>
/// environment variable, never from the repository. The model is <c>AiReceptionist:Model</c>: the cheapest one is the default
/// because the add-on is priced on that (BUSINESS_RULES 6.y17). Reasoning stays at its lowest effort for the same reason.
/// </summary>
public sealed class AnthropicAiModel : IAiModel
{
    public const string DefaultModel = "claude-haiku-5-5";

    private readonly IAnthropicClient? client;
    private readonly string model;

    public AnthropicAiModel(IConfiguration config)
    {
        var key = config["Anthropic:ApiKey"];
        if (string.IsNullOrWhiteSpace(key)) key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        model = string.IsNullOrWhiteSpace(config["AiReceptionist:Model"]) ? DefaultModel : config["AiReceptionist:Model"]!.Trim();
        if (!string.IsNullOrWhiteSpace(key))
        {
            client = new AnthropicClient { ApiKey = key.Trim(), Timeout = TimeSpan.FromSeconds(30), MaxRetries = 1 };
            // Only for a gateway in front of the API, and for tests that run a fake one; the default is Anthropic's own address.
            if (!string.IsNullOrWhiteSpace(config["Anthropic:BaseUrl"])) client = client.WithOptions(o => o with { BaseUrl = config["Anthropic:BaseUrl"]!.Trim() });
        }
    }

    public bool IsConfigured => client is not null;

    public async Task<AiStep> NextAsync(string system, IReadOnlyList<AiMessage> messages, IReadOnlyList<AiTool> tools, CancellationToken ct = default)
    {
        if (client is null) throw new InvalidOperationException("The AI model has no key.");

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = 800,
            System = system,
            Messages = messages.Select(ToParam).ToList(),
            Tools = tools.Select(ToTool).ToList(),
            OutputConfig = new OutputConfig { Effort = Effort.Low },
        }, ct);

        var blocks = new List<AiBlock>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? text))
                blocks.Add(AiBlock.OfText(text.Text));
            else if (block.TryPickToolUse(out ToolUseBlock? use))
                blocks.Add(new AiBlock(AiBlockKind.ToolUse, Id: use.ID, Name: use.Name, Json: JsonSerializer.Serialize(use.Input)));
            else if (block.TryPickThinking(out ThinkingBlock? thinking))
                blocks.Add(new AiBlock(AiBlockKind.Other, Native: (ContentBlockParam)new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature }));
            else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
                blocks.Add(new AiBlock(AiBlockKind.Other, Native: (ContentBlockParam)new RedactedThinkingBlockParam { Data = redacted.Data }));
        }
        return new AiStep(blocks, response.StopReason?.Raw() ?? "", response.Usage.InputTokens, response.Usage.OutputTokens);
    }

    private static MessageParam ToParam(AiMessage message)
    {
        List<ContentBlockParam> content = [];
        foreach (var block in message.Blocks)
        {
            switch (block.Kind)
            {
                case AiBlockKind.Text:
                    content.Add(new TextBlockParam { Text = block.Text });
                    break;
                case AiBlockKind.ToolUse:
                    content.Add(new ToolUseBlockParam
                    {
                        ID = block.Id, Name = block.Name,
                        Input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(block.Json) ?? [],
                    });
                    break;
                case AiBlockKind.ToolResult:
                    content.Add(new ToolResultBlockParam { ToolUseID = block.Id, Content = block.Text, IsError = block.IsError });
                    break;
                case AiBlockKind.Other when block.Native is ContentBlockParam native:
                    content.Add(native);
                    break;
            }
        }
        return message.Role == "assistant"
            ? new AssistantMessageParam { Content = content }
            : new UserMessageParam { Content = content };
    }

    private static ToolUnion ToTool(AiTool tool) => new Tool
    {
        Name = tool.Name,
        Description = tool.Description,
        InputSchema = new()
        {
            Properties = tool.Properties.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)),
            Required = tool.Required.ToList(),
        },
    };
}
