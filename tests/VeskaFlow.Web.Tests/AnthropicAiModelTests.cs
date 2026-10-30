using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using VeskaFlow.Web.Services;

namespace VeskaFlow.Web.Tests;

/// <summary>
/// The real SDK adapter against a fake Messages API on localhost, so the request it sends and the way it reads a reply are
/// checked without a key or any cost. What this cannot show is how the live service answers (see BUILD_PLAN).
/// </summary>
public class AnthropicAiModelTests
{
    private sealed class FakeApi : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly Task loop;
        public string Url { get; }
        public List<JsonDocument> Requests { get; } = [];
        public List<string> Headers { get; } = [];
        public string Response { get; set; } = "";

        public FakeApi()
        {
            var port = new Random().Next(20000, 40000);
            Url = $"http://127.0.0.1:{port}";
            listener.Prefixes.Add(Url + "/");
            listener.Start();
            loop = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await listener.GetContextAsync(); } catch { return; }
                    using var reader = new StreamReader(ctx.Request.InputStream);
                    Requests.Add(JsonDocument.Parse(await reader.ReadToEndAsync()));
                    // The SDK sends the key as a bearer token (the API accepts that as well as x-api-key).
                    Headers.Add($"{ctx.Request.Url!.AbsolutePath} auth={ctx.Request.Headers["Authorization"]} version={ctx.Request.Headers["anthropic-version"]}");
                    var bytes = Encoding.UTF8.GetBytes(Response);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(bytes);
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose() { listener.Stop(); listener.Close(); }
    }

    private const string ToolUseReply = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-5-5",
         "content":[
           {"type":"thinking","thinking":"hmm","signature":"sig-123"},
           {"type":"text","text":"Let me check."},
           {"type":"tool_use","id":"toolu_1","name":"check_slots","input":{"service_id":"s1","date":"2030-03-05"}}],
         "stop_reason":"tool_use","stop_sequence":null,
         "usage":{"input_tokens":120,"output_tokens":30}}
        """;

    private static readonly AiTool Tool = new("check_slots", "List free times.",
        new Dictionary<string, object> { ["service_id"] = new { type = "string" }, ["date"] = new { type = "string" } }, ["service_id", "date"]);

    private static AnthropicAiModel ModelFor(FakeApi api, string? key = "test-key", string? model = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Anthropic:ApiKey"] = key, ["Anthropic:BaseUrl"] = api.Url, ["AiReceptionist:Model"] = model,
        }).Build());

    [Fact]
    public async Task The_request_has_the_model_system_tools_and_lowest_effort_and_the_reply_is_read_back()
    {
        using var api = new FakeApi { Response = ToolUseReply };
        var model = ModelFor(api);
        Assert.True(model.IsConfigured);

        var step = await model.NextAsync("You are a helper.", [AiMessage.User("may slot bukas?")], [Tool]);

        var request = api.Requests.Single().RootElement;
        Assert.Equal("/v1/messages", api.Headers.Single().Split(' ')[0]);
        Assert.Contains("auth=Bearer test-key", api.Headers.Single());
        Assert.Equal(AnthropicAiModel.DefaultModel, request.GetProperty("model").GetString());
        Assert.Equal("claude-haiku-5-5", AnthropicAiModel.DefaultModel);
        Assert.Equal("You are a helper.", request.GetProperty("system").GetString());
        Assert.Equal("low", request.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.False(request.TryGetProperty("temperature", out _));   // sampling settings are rejected by the newer models
        Assert.False(request.TryGetProperty("thinking", out _));
        Assert.False(request.TryGetProperty("tool_choice", out _));   // forced tool use is rejected too
        var tool = request.GetProperty("tools")[0];
        Assert.Equal("check_slots", tool.GetProperty("name").GetString());
        Assert.Equal("object", tool.GetProperty("input_schema").GetProperty("type").GetString());
        Assert.Equal(["service_id", "date"], tool.GetProperty("input_schema").GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        var message = request.GetProperty("messages")[0];
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("may slot bukas?", message.GetProperty("content")[0].GetProperty("text").GetString());

        Assert.Equal("tool_use", step.StopReason);
        Assert.Equal(120, step.InputTokens);
        Assert.Equal(30, step.OutputTokens);
        Assert.Equal([AiBlockKind.Other, AiBlockKind.Text, AiBlockKind.ToolUse], step.Blocks.Select(b => b.Kind));
        var use = step.Blocks[2];
        Assert.Equal("toolu_1", use.Id);
        Assert.Equal("check_slots", use.Name);
        using var input = JsonDocument.Parse(use.Json);
        Assert.Equal("2030-03-05", input.RootElement.GetProperty("date").GetString());
    }

    [Fact]
    public async Task The_models_reasoning_and_tool_use_go_back_unchanged_with_the_tool_result()
    {
        using var api = new FakeApi { Response = ToolUseReply };
        var model = ModelFor(api, model: "claude-haiku-5-5");
        var first = await model.NextAsync("sys", [AiMessage.User("hi")], [Tool]);

        api.Response = ToolUseReply.Replace("tool_use", "end_turn").Replace("\"id\":\"end_turn\"", "\"id\":\"toolu_1\"");
        await model.NextAsync("sys",
        [
            AiMessage.User("hi"),
            new AiMessage("assistant", first.Blocks),
            new AiMessage("user", [AiBlock.ToolResult("toolu_1", "{\"times\":[\"10:00\"]}", false)]),
        ], [Tool]);

        var messages = api.Requests[1].RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        var assistant = messages[1];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        var blocks = assistant.GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(["thinking", "text", "tool_use"], blocks.Select(b => b.GetProperty("type").GetString()));
        Assert.Equal("sig-123", blocks[0].GetProperty("signature").GetString());     // the signature is never altered
        Assert.Equal("toolu_1", blocks[2].GetProperty("id").GetString());
        Assert.Equal("2030-03-05", blocks[2].GetProperty("input").GetProperty("date").GetString());
        var result = messages[2].GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
        Assert.Equal("toolu_1", result.GetProperty("tool_use_id").GetString());
        Assert.Equal("{\"times\":[\"10:00\"]}", result.GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_refusal_and_a_plain_answer_are_told_apart_by_their_stop_reason()
    {
        using var api = new FakeApi { Response = """
            {"id":"msg_2","type":"message","role":"assistant","model":"claude-haiku-5-5","content":[],
             "stop_reason":"refusal","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":0}}
            """ };
        var model = ModelFor(api);
        var refused = await model.NextAsync("sys", [AiMessage.User("hi")], [Tool]);
        Assert.Equal("refusal", refused.StopReason);   // the service checks this exact word

        api.Response = """
            {"id":"msg_3","type":"message","role":"assistant","model":"claude-haiku-5-5","content":[{"type":"text","text":"Hello!"}],
             "stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5}}
            """;
        var plain = await model.NextAsync("sys", [AiMessage.User("hi")], [Tool]);
        Assert.Equal("end_turn", plain.StopReason);
        Assert.Equal("Hello!", Assert.Single(plain.Blocks).Text);
    }

    [Fact]
    public async Task A_different_model_can_be_chosen_in_settings_and_no_key_means_not_configured()
    {
        using var api = new FakeApi { Response = ToolUseReply };
        await ModelFor(api, model: "claude-sonnet-5-5").NextAsync("sys", [AiMessage.User("hi")], [Tool]);
        Assert.Equal("claude-sonnet-5-5", api.Requests.Single().RootElement.GetProperty("model").GetString());

        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        var none = ModelFor(api, key: null);
        Assert.False(none.IsConfigured);
        await Assert.ThrowsAsync<InvalidOperationException>(() => none.NextAsync("sys", [AiMessage.User("hi")], [Tool]));
    }
}
