using System.Text.Json;
using System.Text.Json.Nodes;
using ModelBench.Core.Config;
using ModelBench.Core.Domain;
using ModelBench.Core.Grading;
using ModelBench.Core.Providers;
using ModelBench.Tests.Support;

namespace ModelBench.Tests.Providers;

public static class AdapterKit
{
    /// <summary>One provider "p" with model "m"; <paramref name="fields"/> are extra model fields, e.g. "\"reasoning\":true".</summary>
    public static ResolvedModel Model(string api, string baseUrl, string fields = "", string? variant = null, string providerFields = "")
        => ModelCatalog.Parse($$$$"""{"providers":{"p":{"api":"{{{{api}}}}","baseUrl":"{{{{baseUrl}}}}","apiKey":"k-secret"{{{{providerFields}}}},"models":[{"id":"m","maxTokens":100{{{{(fields.Length > 0 ? "," + fields : "")}}}}}]}}}""")
            .Resolve(variant is null ? "p/m" : $"p/m@{variant}");

    public static readonly ToolDefinition Get = new("get", "Gets", JsonDocument.Parse("""{"type":"object","properties":{"a":{"type":"number"}}}""").RootElement.Clone());

    public static FakeReply Sse(JsonObject[] events) => new(200, events.Select(e => e.ToJsonString()).ToList());

    /// <summary>A conversation with one assistant turn that made two tool calls, answered by two tool results.</summary>
    public static ModelRequest ToolConversation(string? providerState = null) => new(
    [
        new ChatMessage(Role.System, "sys"),
        new ChatMessage(Role.User, "go"),
        new ChatMessage(Role.Assistant, "calling", [new ToolCall("c1", "get", "{\"a\":1}"), new ToolCall("c2", "get", "{\"a\":2}")], ProviderState: providerState),
        new ChatMessage(Role.Tool, "{\"v\":1}", ToolCallId: "c1", ToolName: "get"),
        new ChatMessage(Role.Tool, "plain text", ToolCallId: "c2", ToolName: "get"),
    ], [Get]);

    public static readonly ModelRequest Hello = new([new ChatMessage(Role.System, "sys"), new ChatMessage(Role.User, "hi")], []);

    public static JsonObject LastBody(FakeServer s) => JsonNode.Parse(s.Bodies[^1])!.AsObject();

    public static async Task AssertErrors(Func<string, IModelProvider> make, string path, params (int Status, bool Retryable)[] cases)
    {
        foreach (var (status, retryable) in cases)
        {
            await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Error(status, "details-" + new string('x', 600)), path);
            var ex = await Assert.ThrowsAsync<InfraException>(() => make(server.BaseUrl).CompleteAsync(Hello, default));
            Assert.Equal(retryable, ex.Retryable);
            Assert.Contains($"HTTP {status}", ex.Message);
            Assert.True(ex.Message.Length < 600);
        }
    }
}

public class AnthropicMessagesProviderTests
{
    private const string Path = "/v1/messages";

    private static AnthropicMessagesProvider Provider(string url, string fields = "", string? variant = null, string providerFields = "")
        => new(AdapterKit.Model("anthropic-messages", url, fields, variant, providerFields));

    private static FakeReply ToolTurn() => AdapterKit.Sse([
        new() { ["type"] = "message_start", ["message"] = new JsonObject { ["usage"] = new JsonObject { ["input_tokens"] = 25, ["output_tokens"] = 1 } } },
        new() { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "thinking", ["thinking"] = "" } },
        new() { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = "Let me " } },
        new() { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = "think." } },
        new() { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "signature_delta", ["signature"] = "SIG==" } },
        new() { ["type"] = "content_block_stop", ["index"] = 0 },
        new() { ["type"] = "content_block_start", ["index"] = 1, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" } },
        new() { ["type"] = "content_block_delta", ["index"] = 1, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = "Hel" } },
        new() { ["type"] = "content_block_delta", ["index"] = 1, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = "lo" } },
        new() { ["type"] = "content_block_stop", ["index"] = 1 },
        new() { ["type"] = "content_block_start", ["index"] = 2, ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_1", ["name"] = "get", ["input"] = new JsonObject() } },
        new() { ["type"] = "content_block_delta", ["index"] = 2, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = "{\"a\":" } },
        new() { ["type"] = "content_block_delta", ["index"] = 2, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = "1}" } },
        new() { ["type"] = "content_block_stop", ["index"] = 2 },
        new() { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = "tool_use" }, ["usage"] = new JsonObject { ["output_tokens"] = 42 } },
        new() { ["type"] = "message_stop" }]);

    [Fact] public async Task StreamsThinkingTextToolCallsUsageAndProviderState()
    {
        await using var server = await FakeServer.StartAsync((_, _) => ToolTurn(), Path, sendDone: false);
        var r = await Provider(server.BaseUrl).CompleteAsync(AdapterKit.Hello, default);
        Assert.Equal("Hello", r.Text);
        Assert.Equal("Let me think.", r.ReasoningText);
        var call = Assert.Single(r.ToolCalls);
        Assert.Equal(("toolu_1", "get", "{\"a\":1}"), (call.Id, call.Name, call.ArgumentsJson));
        Assert.Equal("tool_use", r.FinishReason);
        Assert.Equal(new Usage(25, 42, null), r.Usage);
        Assert.NotNull(r.Timing.TimeToFirstToken);
        var expected = JsonNode.Parse("""
            [{"type":"thinking","thinking":"Let me think.","signature":"SIG=="},{"type":"text","text":"Hello"},
             {"type":"tool_use","id":"toolu_1","name":"get","input":{"a":1}}]
            """);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(r.ProviderState!)));
    }

    [Fact] public async Task RefusalIsModelBehaviourAndMissingUsageIsNull()
    {
        await using var server = await FakeServer.StartAsync((_, _) => AdapterKit.Sse([
            new() { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" } },
            new() { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = "no" } },
            new() { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = "refusal" } }]), Path, false);
        var r = await Provider(server.BaseUrl).CompleteAsync(AdapterKit.Hello, default);
        Assert.Equal("refusal", r.FinishReason);
        Assert.Equal(Usage.Unknown, r.Usage);
    }

    [Fact] public async Task RequestHeadersAndBodyShape()
    {
        await using var server = await FakeServer.StartAsync((_, _) => ToolTurn(), Path, false);
        // baseUrl already ends in /v1 (FakeServer.BaseUrl): it must not be doubled.
        await Provider(server.BaseUrl, providerFields: "").CompleteAsync(AdapterKit.ToolConversation(), default);
        var h = server.Headers[0];
        Assert.Equal("k-secret", h["x-api-key"]);
        Assert.Equal("2026-10-21", h["anthropic-version"]);
        Assert.False(h.ContainsKey("anthropic-beta"));
        var body = AdapterKit.LastBody(server);
        Assert.Equal((100, true, "sys"), ((int)body["max_tokens"]!, (bool)body["stream"]!, (string)body["system"]!));
        Assert.Null(body["thinking"]);
        Assert.Null(body["output_config"]);
        Assert.Equal("object", (string?)body["tools"]![0]!["input_schema"]!["type"]);
        var msgs = body["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user"], msgs.Select(m => (string?)m!["role"]));
        var asst = msgs[1]!["content"]!.AsArray();
        Assert.Equal(["text", "tool_use", "tool_use"], asst.Select(b => (string?)b!["type"]));
        Assert.Equal(1, (int?)asst[1]!["input"]!["a"]);
        var results = msgs[2]!["content"]!.AsArray(); // all results of the turn in ONE user message
        Assert.Equal(["tool_result", "tool_result"], results.Select(b => (string?)b!["type"]));
        Assert.Equal(["c1", "c2"], results.Select(b => (string?)b!["tool_use_id"]));
        Assert.Equal("plain text", (string?)results[1]!["content"]);
    }

    [Fact] public async Task VersionAndBetaCompatAreSentAndBaseUrlWithoutV1Works()
    {
        await using var server = await FakeServer.StartAsync((_, _) => ToolTurn(), Path, false);
        var model = AdapterKit.Model("anthropic-messages", server.BaseUrl[..^3], "\"compat\":{\"anthropicVersion\":\"2025-01-01\",\"anthropicBeta\":\"x-beta-1\"}");
        await new AnthropicMessagesProvider(model).CompleteAsync(AdapterKit.Hello, default);
        Assert.Equal(("2025-01-01", "x-beta-1"), (server.Headers[0]["anthropic-version"], server.Headers[0]["anthropic-beta"]));
    }

    [Theory]
    [InlineData("""{"effort":"high"}""", "high", null, null)]
    [InlineData("""{"reasoningEffort":"low"}""", "low", null, null)]
    [InlineData("""{"thinking":"adaptive"}""", null, "adaptive", null)]
    [InlineData("""{"thinkingBudget":2048}""", null, "enabled", 2048)]
    [InlineData("""{}""", null, null, null)]
    public async Task ThinkingVariantsMapToRequestFields(string variant, string? effort, string? thinkingType, int? budget)
    {
        await using var server = await FakeServer.StartAsync((_, _) => ToolTurn(), Path, false);
        await Provider(server.BaseUrl, $"\"variants\":{{\"v\":{variant}}}", "v").CompleteAsync(AdapterKit.Hello, default);
        var body = AdapterKit.LastBody(server);
        Assert.Equal(effort, (string?)body["output_config"]?["effort"]);
        Assert.Equal(thinkingType, (string?)body["thinking"]?["type"]);
        Assert.Equal(budget, (int?)body["thinking"]?["budget_tokens"]);
        if (thinkingType == "adaptive") Assert.Equal("summarized", (string?)body["thinking"]!["display"]);
    }

    [Fact] public void SamplingParamsAreRejectedAtConstructionForCurrentApiVersions()
    {
        foreach (var p in new[] { "temperature", "top_p", "top_k" })
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Provider("http://x", $"\"samplingParams\":{{\"{p}\":0.5}}"));
            Assert.Contains(p, ex.Message);
            Assert.Contains("remove", ex.Message);
        }
        Provider("http://x", "\"samplingParams\":{\"max_foo\":1}"); // other params are not rejected
    }

    [Fact] public async Task OlderApiVersionsStillAcceptSampling()
    {
        await using var server = await FakeServer.StartAsync((_, _) => ToolTurn(), Path, false);
        var model = AdapterKit.Model("anthropic-messages", server.BaseUrl, "\"samplingParams\":{\"temperature\":0.5},\"compat\":{\"anthropicVersion\":\"2025-06-01\"}");
        await new AnthropicMessagesProvider(model).CompleteAsync(AdapterKit.Hello, default);
        Assert.Equal(0.5, (double?)AdapterKit.LastBody(server)["temperature"]);
    }

    [Fact] public async Task ProviderStateIsReplayedVerbatim()
    {
        await using var server = await FakeServer.StartAsync((_, _) => ToolTurn(), Path, false);
        var provider = Provider(server.BaseUrl);
        var first = await provider.CompleteAsync(AdapterKit.Hello, default);
        await provider.CompleteAsync(AdapterKit.ToolConversation(first.ProviderState), default);
        var content = AdapterKit.LastBody(server)["messages"]![1]!["content"];
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(first.ProviderState!), content));
        Assert.Contains("SIG==", content!.ToJsonString());
    }

    [Fact] public Task HttpErrorsAreClassified()
        => AdapterKit.AssertErrors(u => Provider(u), Path, (429, true), (529, true), (500, true), (408, true), (400, false), (401, false));

    [Theory]
    [InlineData("overloaded_error", true)]
    [InlineData("invalid_request_error", false)]
    public async Task StreamErrorEvents(string type, bool retryable)
    {
        await using var server = await FakeServer.StartAsync((_, _) => AdapterKit.Sse([
            new() { ["type"] = "error", ["error"] = new JsonObject { ["type"] = type, ["message"] = "bad things" } }]), Path, false);
        var ex = await Assert.ThrowsAsync<InfraException>(() => Provider(server.BaseUrl).CompleteAsync(AdapterKit.Hello, default));
        Assert.Equal(retryable, ex.Retryable);
        Assert.Contains("bad things", ex.Message);
    }
}

public class OpenAiResponsesProviderTests
{
    private const string Path = "/v1/responses";

    private static OpenAiResponsesProvider Provider(string url, string fields = "\"reasoning\":true", string? variant = null)
        => new(AdapterKit.Model("openai-responses", url, fields, variant));

    private static FakeReply Turn() => AdapterKit.Sse([
        new() { ["type"] = "response.reasoning_summary_text.delta", ["delta"] = "think " },
        new() { ["type"] = "response.reasoning_summary_text.delta", ["delta"] = "hard" },
        new() { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = new JsonObject { ["type"] = "reasoning", ["id"] = "rs_1", ["encrypted_content"] = "ENC", ["summary"] = new JsonArray() } },
        new() { ["type"] = "response.output_text.delta", ["delta"] = "Hel" },
        new() { ["type"] = "response.output_text.delta", ["delta"] = "lo" },
        new() { ["type"] = "response.output_item.done", ["output_index"] = 1, ["item"] = new JsonObject { ["type"] = "message", ["id"] = "msg_1", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "Hello" }) } },
        new() { ["type"] = "response.function_call_arguments.delta", ["delta"] = "{\"a\":" },
        new() { ["type"] = "response.output_item.done", ["output_index"] = 2, ["item"] = new JsonObject { ["type"] = "function_call", ["id"] = "fc_1", ["call_id"] = "call_a", ["name"] = "get", ["arguments"] = "{\"a\":1}" } },
        new()
        {
            ["type"] = "response.completed",
            ["response"] = new JsonObject
            {
                ["status"] = "completed",
                ["usage"] = new JsonObject { ["input_tokens"] = 11, ["output_tokens"] = 22, ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 7 } },
            },
        }]);

    [Fact] public async Task StreamsTextReasoningToolCallsUsageAndProviderState()
    {
        await using var server = await FakeServer.StartAsync((_, _) => Turn(), Path, false);
        var r = await Provider(server.BaseUrl).CompleteAsync(AdapterKit.Hello, default);
        Assert.Equal("Hello", r.Text);
        Assert.Equal("think hard", r.ReasoningText);
        var call = Assert.Single(r.ToolCalls);
        Assert.Equal(("call_a", "get", "{\"a\":1}"), (call.Id, call.Name, call.ArgumentsJson));
        Assert.Equal(new Usage(11, 22, 7), r.Usage);
        Assert.Equal("completed", r.FinishReason);
        Assert.NotNull(r.Timing.TimeToFirstToken);
        Assert.Equal(["reasoning", "message", "function_call"], JsonNode.Parse(r.ProviderState!)!.AsArray().Select(i => (string?)i!["type"]));
        Assert.Equal("ENC", (string?)JsonNode.Parse(r.ProviderState!)![0]!["encrypted_content"]);
    }

    [Fact] public async Task UsageStaysNullWhenNotReported()
    {
        await using var server = await FakeServer.StartAsync((_, _) => AdapterKit.Sse([
            new() { ["type"] = "response.output_text.delta", ["delta"] = "x" },
            new() { ["type"] = "response.completed", ["response"] = new JsonObject { ["status"] = "completed" } }]), Path, false);
        Assert.Equal(Usage.Unknown, (await Provider(server.BaseUrl).CompleteAsync(AdapterKit.Hello, default)).Usage);
    }

    [Fact] public async Task RequestBodyShapeAndInputItems()
    {
        await using var server = await FakeServer.StartAsync((_, _) => Turn(), Path, false);
        await Provider(server.BaseUrl, "\"reasoning\":true,\"samplingParams\":{\"temperature\":0.3},\"extra\":{\"temperature\":0.1},\"variants\":{\"low\":{\"reasoningEffort\":\"low\"}}", "low")
            .CompleteAsync(AdapterKit.ToolConversation(), default);
        Assert.Equal("Bearer k-secret", server.AuthHeaders[0]);
        var body = AdapterKit.LastBody(server);
        Assert.Equal(("m", true, false, "sys", 100), ((string)body["model"]!, (bool)body["stream"]!, (bool)body["store"]!, (string)body["instructions"]!, (int)body["max_output_tokens"]!));
        Assert.Equal(["reasoning.encrypted_content"], body["include"]!.AsArray().Select(x => (string?)x));
        Assert.Equal(0.1, (double?)body["temperature"]); // extra merged last
        Assert.Equal("low", (string?)body["reasoning"]!["effort"]);
        Assert.Equal("function", (string?)body["tools"]![0]!["type"]);
        Assert.Equal("get", (string?)body["tools"]![0]!["name"]);
        Assert.Equal("object", (string?)body["tools"]![0]!["parameters"]!["type"]);
        var input = body["input"]!.AsArray();
        Assert.Equal("user", (string?)input[0]!["role"]);
        Assert.Equal(("assistant", "calling"), (input[1]!["role"]!.ToString(), input[1]!["content"]!.ToString()));
        Assert.Equal(["function_call", "function_call"], input.Skip(2).Take(2).Select(i => (string?)i!["type"]));
        Assert.Equal(("c1", "get", "{\"a\":1}"), ((string?)input[2]!["call_id"], (string?)input[2]!["name"], (string?)input[2]!["arguments"]));
        Assert.Equal(["function_call_output", "function_call_output"], input.Skip(4).Select(i => (string?)i!["type"]));
        Assert.Equal(("c2", "plain text"), ((string?)input[5]!["call_id"], (string?)input[5]!["output"]));
    }

    [Theory]
    [InlineData("\"reasoning\":true", null, true, null)]
    [InlineData("\"reasoning\":false", null, false, null)]
    [InlineData("\"reasoning\":true,\"variants\":{\"h\":{\"reasoningEffort\":\"high\"}}", "h", true, "high")]
    public async Task ReasoningFlagAndEffort(string fields, string? variant, bool include, string? effort)
    {
        await using var server = await FakeServer.StartAsync((_, _) => Turn(), Path, false);
        await Provider(server.BaseUrl, fields, variant).CompleteAsync(AdapterKit.Hello, default);
        var body = AdapterKit.LastBody(server);
        Assert.Equal(include, body["include"] is not null);
        Assert.Equal(effort, (string?)body["reasoning"]?["effort"]);
    }

    [Fact] public async Task ProviderStateItemsAreReplayedVerbatim()
    {
        await using var server = await FakeServer.StartAsync((_, _) => Turn(), Path, false);
        var provider = Provider(server.BaseUrl);
        var first = await provider.CompleteAsync(AdapterKit.Hello, default);
        await provider.CompleteAsync(AdapterKit.ToolConversation(first.ProviderState), default);
        var input = AdapterKit.LastBody(server)["input"]!.AsArray();
        var items = JsonNode.Parse(first.ProviderState!)!.AsArray();
        for (var i = 0; i < items.Count; i++) Assert.True(JsonNode.DeepEquals(items[i], input[1 + i]));
        Assert.Equal(1 + items.Count + 2, input.Count); // user + replayed items + two function_call_output
        Assert.DoesNotContain(input, i => (string?)i!["role"] == "assistant" && i["content"]?.ToString() == "calling");
    }

    [Fact] public Task HttpErrorsAreClassified()
        => AdapterKit.AssertErrors(u => Provider(u), Path, (429, true), (503, true), (400, false), (404, false));

    [Theory]
    [InlineData("server_error", true)]
    [InlineData("invalid_prompt", false)]
    public async Task FailedResponses(string code, bool retryable)
    {
        await using var server = await FakeServer.StartAsync((_, _) => AdapterKit.Sse([
            new() { ["type"] = "response.failed", ["response"] = new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = "nope" } } }]), Path, false);
        var ex = await Assert.ThrowsAsync<InfraException>(() => Provider(server.BaseUrl).CompleteAsync(AdapterKit.Hello, default));
        Assert.Equal(retryable, ex.Retryable);
    }
}

public class GoogleGenerativeAiProviderTests
{
    private const string Path = "/v1/models/{**rest}";

    private static GoogleGenerativeAiProvider Provider(string url, string fields = "\"reasoning\":true", string? variant = null)
        => new(AdapterKit.Model("google-generative-ai", url, fields, variant));

    private static JsonObject Chunk(JsonArray parts, string? finish = null, JsonObject? usage = null)
    {
        var o = new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["content"] = new JsonObject { ["role"] = "model", ["parts"] = parts } }) };
        if (finish is not null) o["candidates"]![0]!["finishReason"] = finish;
        if (usage is not null) o["usageMetadata"] = usage;
        return o;
    }

    private static FakeReply Turn() => AdapterKit.Sse([
        Chunk(new JsonArray(new JsonObject { ["text"] = "think ", ["thought"] = true })),
        Chunk(new JsonArray(new JsonObject { ["text"] = "Hel" })),
        Chunk(new JsonArray(new JsonObject { ["text"] = "lo" })),
        Chunk(new JsonArray(
            new JsonObject { ["functionCall"] = new JsonObject { ["name"] = "get", ["args"] = new JsonObject { ["a"] = 1 } }, ["thoughtSignature"] = "SIGN" },
            new JsonObject { ["functionCall"] = new JsonObject { ["name"] = "get", ["args"] = new JsonObject { ["a"] = 2 } } }),
            "STOP", new JsonObject { ["promptTokenCount"] = 12, ["candidatesTokenCount"] = 20, ["thoughtsTokenCount"] = 8 })]);

    [Fact] public async Task StreamsThoughtsTextCallsUsageAndParts()
    {
        await using var server = await FakeServer.StartAsync((_, _) => Turn(), Path, false);
        var r = await Provider(server.BaseUrl).CompleteAsync(AdapterKit.Hello, default);
        Assert.Equal("Hello", r.Text);
        Assert.Equal("think ", r.ReasoningText);
        Assert.Equal([("call_0", "{\"a\":1}"), ("call_1", "{\"a\":2}")], r.ToolCalls.Select(c => (c.Id, c.ArgumentsJson)));
        Assert.Equal("STOP", r.FinishReason);
        Assert.Equal(new Usage(12, 28, 8), r.Usage); // output = candidates + thoughts
        Assert.NotNull(r.Timing.TimeToFirstToken);
        var parts = JsonNode.Parse(r.ProviderState!)!.AsArray();
        Assert.Equal(5, parts.Count);
        Assert.Equal("SIGN", (string?)parts[3]!["thoughtSignature"]);
    }

    [Fact] public async Task UsageStaysNullWhenNotReported()
    {
        await using var server = await FakeServer.StartAsync((_, _) => AdapterKit.Sse([Chunk(new JsonArray(new JsonObject { ["text"] = "x" }), "STOP")]), Path, false);
        Assert.Equal(Usage.Unknown, (await Provider(server.BaseUrl).CompleteAsync(AdapterKit.Hello, default)).Usage);
    }

    [Fact] public async Task RequestBodyShape()
    {
        await using var server = await FakeServer.StartAsync((_, _) => Turn(), Path, false);
        await Provider(server.BaseUrl, "\"reasoning\":true,\"samplingParams\":{\"temperature\":0.4}").CompleteAsync(AdapterKit.ToolConversation(), default);
        Assert.Equal("k-secret", server.Headers[0]["x-goog-api-key"]);
        var body = AdapterKit.LastBody(server);
        Assert.Equal("sys", (string?)body["systemInstruction"]!["parts"]![0]!["text"]);
        Assert.Equal("get", (string?)body["tools"]![0]!["functionDeclarations"]![0]!["name"]);
        Assert.Equal("object", (string?)body["tools"]![0]!["functionDeclarations"]![0]!["parameters"]!["type"]);
        var cfg = body["generationConfig"]!;
        Assert.Equal((100, 0.4, true), ((int)cfg["maxOutputTokens"]!, (double)cfg["temperature"]!, (bool)cfg["thinkingConfig"]!["includeThoughts"]!));
        var contents = body["contents"]!.AsArray();
        Assert.Equal(["user", "model", "user"], contents.Select(c => (string?)c!["role"]));
        var model = contents[1]!["parts"]!.AsArray();
        Assert.Equal("calling", (string?)model[0]!["text"]);
        Assert.Equal(1, (int?)model[1]!["functionCall"]!["args"]!["a"]);
        var responses = contents[2]!["parts"]!.AsArray(); // both results in one turn
        Assert.Equal(2, responses.Count);
        Assert.Equal(1, (int?)responses[0]!["functionResponse"]!["response"]!["result"]!["v"]); // parsed JSON stays JSON
        Assert.Equal("plain text", (string?)responses[1]!["functionResponse"]!["response"]!["result"]); // other text stays a string
        Assert.Equal("get", (string?)responses[1]!["functionResponse"]!["name"]);
    }

    [Theory]
    [InlineData("\"reasoning\":true,\"variants\":{\"v\":{\"thinkingBudget\":1024}}", "v", "thinkingBudget", "1024", true)]
    [InlineData("\"reasoning\":true,\"variants\":{\"v\":{\"thinkingLevel\":\"low\"}}", "v", "thinkingLevel", "low", true)]
    [InlineData("\"reasoning\":false,\"variants\":{\"v\":{\"reasoningEffort\":\"high\"}}", "v", "thinkingLevel", "high", false)]
    public async Task ThinkingConfig(string fields, string variant, string key, string value, bool includeThoughts)
    {
        await using var server = await FakeServer.StartAsync((_, _) => Turn(), Path, false);
        await Provider(server.BaseUrl, fields, variant).CompleteAsync(AdapterKit.Hello, default);
        var cfg = AdapterKit.LastBody(server)["generationConfig"]!["thinkingConfig"]!;
        Assert.Equal(value, cfg[key]!.ToString());
        Assert.Equal(includeThoughts, cfg["includeThoughts"] is not null);
    }

    [Fact] public async Task NoThinkingConfigForPlainModels()
    {
        await using var server = await FakeServer.StartAsync((_, _) => Turn(), Path, false);
        await Provider(server.BaseUrl, "").CompleteAsync(AdapterKit.Hello, default);
        Assert.Null(AdapterKit.LastBody(server)["generationConfig"]!["thinkingConfig"]);
    }

    [Fact] public async Task ProviderStatePartsAreReplayedVerbatim()
    {
        await using var server = await FakeServer.StartAsync((_, _) => Turn(), Path, false);
        var provider = Provider(server.BaseUrl);
        var first = await provider.CompleteAsync(AdapterKit.Hello, default);
        await provider.CompleteAsync(AdapterKit.ToolConversation(first.ProviderState), default);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(first.ProviderState!), AdapterKit.LastBody(server)["contents"]![1]!["parts"]));
    }

    [Fact] public Task HttpErrorsAreClassified()
        => AdapterKit.AssertErrors(u => Provider(u), Path, (429, true), (503, true), (400, false), (403, false));
}

/// <summary>Opt-in live checks: each needs its API key (and optionally MODELBENCH_SMOKE_*_MODEL) in the environment.</summary>
public class LiveSmokeTests
{
    private static async Task Smoke(string keyVar, string modelVar, string defaultModel, string api, string baseUrl, Func<ResolvedModel, IModelProvider> make)
    {
        var key = Environment.GetEnvironmentVariable(keyVar);
        if (string.IsNullOrEmpty(key)) { Assert.Skip($"{keyVar} is not set"); return; }
        var model = ModelCatalog.Parse($$$$"""{"providers":{"p":{"api":"{{{{api}}}}","baseUrl":"{{{{baseUrl}}}}","apiKey":"{{{{key}}}}","models":[{"id":"{{{{Environment.GetEnvironmentVariable(modelVar) ?? defaultModel}}}}","maxTokens":2000}]}}}""")
            .Resolve($"p/{Environment.GetEnvironmentVariable(modelVar) ?? defaultModel}");
        var r = await make(model).CompleteAsync(new ModelRequest([new ChatMessage(Role.User, "Reply with the single word: pong")], []), default);
        Assert.False(string.IsNullOrWhiteSpace(r.Text));
        Assert.True(r.Usage.InputTokens > 0 && r.Usage.OutputTokens > 0, "usage was not reported");
    }

    [Fact] public Task OpenAiResponses() => Smoke("OPENAI_API_KEY", "MODELBENCH_SMOKE_OPENAI_MODEL", "gpt-5.2", "openai-responses", "https://api.openai.com/v1", m => new OpenAiResponsesProvider(m));
    [Fact] public Task Anthropic() => Smoke("ANTHROPIC_API_KEY", "MODELBENCH_SMOKE_ANTHROPIC_MODEL", "claude-sonnet-5-5", "anthropic-messages", "https://api.anthropic.com", m => new AnthropicMessagesProvider(m));
    [Fact] public Task Gemini() => Smoke("GEMINI_API_KEY", "MODELBENCH_SMOKE_GEMINI_MODEL", "gemini-3-flash", "google-generative-ai", "https://generativelanguage.googleapis.com/v1beta", m => new GoogleGenerativeAiProvider(m));
}
