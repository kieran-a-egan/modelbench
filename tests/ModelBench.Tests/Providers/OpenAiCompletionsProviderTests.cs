using System.Text.Json.Nodes;
using ModelBench.Core.Domain;
using ModelBench.Core.Grading;
using ModelBench.Core.Providers;
using ModelBench.Tests.Support;

namespace ModelBench.Tests.Providers;

public class OpenAiCompletionsProviderTests
{
    private static readonly ModelRequest Request = new([new ChatMessage(Role.System, "sys"), new ChatMessage(Role.User, "hi")], []);

    [Fact] public async Task StreamsTextReasoningToolCallsAndUsage()
    {
        await using var server = await FakeServer.StartAsync((_, _) => new FakeReply(200,
        [
            FakeReply.Delta(new JsonObject { ["reasoning_content"] = "think1 " }),
            FakeReply.Delta(new JsonObject { ["reasoning"] = "think2" }),
            FakeReply.Delta(new JsonObject { ["content"] = "Hel" }),
            FakeReply.Delta(new JsonObject { ["content"] = "lo" }),
            FakeReply.Delta(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["id"] = "c1", ["function"] = new JsonObject { ["name"] = "get", ["arguments"] = "{\"a\":" } }) }),
            FakeReply.Delta(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["function"] = new JsonObject { ["arguments"] = "1}" } }) }),
            FakeReply.Delta([], "tool_calls"),
            FakeReply.UsageChunk(11, 22, 7),
        ]));
        var r = await new OpenAiCompletionsProvider(TestKit.Model(server.BaseUrl)).CompleteAsync(Request, default);

        Assert.Equal("Hello", r.Text);
        Assert.Equal("think1 think2", r.ReasoningText);
        var call = Assert.Single(r.ToolCalls);
        Assert.Equal(("c1", "get", "{\"a\":1}"), (call.Id, call.Name, call.ArgumentsJson));
        Assert.Equal("tool_calls", r.FinishReason);
        Assert.Equal(new Usage(11, 22, 7), r.Usage);
        Assert.NotNull(r.Timing.TimeToFirstToken);
        Assert.True(r.Timing.TimeToFirstToken <= r.Timing.Total);
        Assert.Equal(server.Bodies[0], r.RawRequest);
        Assert.Contains("Hel", r.RawResponse);
    }

    [Fact] public async Task ReasoningTokensAreNullWhenNotReported()
    {
        await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Text("x", 3, 4));
        var r = await new OpenAiCompletionsProvider(TestKit.Model(server.BaseUrl)).CompleteAsync(Request, default);
        Assert.Equal(new Usage(3, 4, null), r.Usage);
    }

    [Fact] public async Task MissingUsageStaysUnknown()
    {
        await using var server = await FakeServer.StartAsync((_, _) => new FakeReply(200, [FakeReply.Delta(new JsonObject { ["content"] = "x" }), FakeReply.Delta([], "stop")]));
        var r = await new OpenAiCompletionsProvider(TestKit.Model(server.BaseUrl)).CompleteAsync(Request, default);
        Assert.Equal(Usage.Unknown, r.Usage);
    }

    [Fact] public async Task RequestBodyCarriesTokensFieldEffortSamplingAndStreamOptions()
    {
        await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Text("x"));
        await new OpenAiCompletionsProvider(TestKit.Model(server.BaseUrl)).CompleteAsync(Request, default);
        var body = JsonNode.Parse(server.Bodies.Single())!.AsObject();
        Assert.Equal("m1", (string?)body["model"]);
        Assert.Equal(512, (int?)body["max_tokens"]);
        Assert.Null(body["max_completion_tokens"]);
        Assert.Equal("low", (string?)body["reasoning_effort"]);
        Assert.Equal(0.7, (double?)body["temperature"]);
        Assert.True((bool?)body["stream"]);
        Assert.True((bool?)body["stream_options"]!["include_usage"]);
        Assert.Equal(["system", "user"], body["messages"]!.AsArray().Select(m => (string?)m!["role"]));
        Assert.Null(body["tools"]);
    }

    [Fact] public async Task DefaultsToMaxCompletionTokensAndMergesExtraLast()
    {
        await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Text("x"));
        var model = ModelBench.Core.Config.ModelCatalog.Parse($$$$"""{"providers":{"p":{"api":"openai-completions","baseUrl":"{{{{server.BaseUrl}}}}","models":[{"id":"m","maxTokens":9,"samplingParams":{"temperature":1},"extra":{"temperature":0.1,"seed":5}}]}}}""").Resolve("p/m");
        await new OpenAiCompletionsProvider(model).CompleteAsync(Request, default);
        var body = JsonNode.Parse(server.Bodies.Single())!.AsObject();
        Assert.Equal(9, (int?)body["max_completion_tokens"]);
        Assert.Equal(0.1, (double?)body["temperature"]);
        Assert.Equal(5, (int?)body["seed"]);
        Assert.Null(body["reasoning_effort"]);
    }

    [Fact] public async Task ReasoningEffortNoneIsOmittedForNonReasoningModels()
    {
        await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Text("x"));
        var model = ModelBench.Core.Config.ModelCatalog.Parse($$$$"""{"providers":{"p":{"api":"openai-completions","baseUrl":"{{{{server.BaseUrl}}}}","models":[{"id":"m","reasoning":false,"compat":{"supportsReasoningEffort":true},"variants":{"off":{"reasoningEffort":"none"}}}]}}}""").Resolve("p/m@off");
        await new OpenAiCompletionsProvider(model).CompleteAsync(Request, default);
        Assert.Null(JsonNode.Parse(server.Bodies.Single())!["reasoning_effort"]);
    }

    [Fact] public async Task ToolsAndToolMessagesAreMapped()
    {
        await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Text("x"));
        var schema = System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}""").RootElement.Clone();
        var request = new ModelRequest(
        [
            new ChatMessage(Role.User, "go"),
            new ChatMessage(Role.Assistant, null, [new ToolCall("c1", "search", "{\"q\":\"a\"}")]),
            new ChatMessage(Role.Tool, "result", ToolCallId: "c1", ToolName: "search"),
        ], [new ToolDefinition("search", "Searches", schema)]);
        await new OpenAiCompletionsProvider(TestKit.Model(server.BaseUrl)).CompleteAsync(request, default);
        var body = JsonNode.Parse(server.Bodies.Single())!.AsObject();
        Assert.Equal("search", (string?)body["tools"]![0]!["function"]!["name"]);
        Assert.Equal("object", (string?)body["tools"]![0]!["function"]!["parameters"]!["type"]);
        var msgs = body["messages"]!.AsArray();
        Assert.Equal("c1", (string?)msgs[1]!["tool_calls"]![0]!["id"]);
        Assert.Equal("{\"q\":\"a\"}", (string?)msgs[1]!["tool_calls"]![0]!["function"]!["arguments"]);
        Assert.Equal("c1", (string?)msgs[2]!["tool_call_id"]);
    }

    [Fact] public async Task BearerAuthUnlessKeyIsNone()
    {
        await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Text("x"));
        await new OpenAiCompletionsProvider(TestKit.Model(server.BaseUrl, apiKey: "none")).CompleteAsync(Request, default);
        await new OpenAiCompletionsProvider(TestKit.Model(server.BaseUrl, apiKey: "k-123")).CompleteAsync(Request, default);
        Assert.Equal([null, "Bearer k-123"], server.AuthHeaders);
        Assert.DoesNotContain("k-123", server.Bodies[1]); // secrets never appear in bodies
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(503, true)]
    [InlineData(408, true)]
    [InlineData(500, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    public async Task HttpErrorsAreClassified(int status, bool retryable)
    {
        await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Error(status, "details-" + new string('x', 600)));
        var ex = await Assert.ThrowsAsync<InfraException>(() => new OpenAiCompletionsProvider(TestKit.Model(server.BaseUrl)).CompleteAsync(Request, default));
        Assert.Equal(retryable, ex.Retryable);
        Assert.Contains($"HTTP {status}", ex.Message);
        Assert.Contains("details-", ex.Message);
        Assert.True(ex.Message.Length < 600, "body is truncated to 500 characters");
    }

    [Fact] public async Task ConnectionFailureIsRetryableInfra()
    {
        string url;
        await using (var server = await FakeServer.StartAsync((_, _) => FakeReply.Text("x"))) url = server.BaseUrl;
        var ex = await Assert.ThrowsAsync<InfraException>(() => new OpenAiCompletionsProvider(TestKit.Model(url)).CompleteAsync(Request, default));
        Assert.True(ex.Retryable);
    }

    private static Task<ModelResponse> Run(FakeServer server) => new OpenAiCompletionsProvider(TestKit.Model(server.BaseUrl)).CompleteAsync(Request, default);

    [Fact] public async Task MidStreamErrorChunkIsRetryableInfraWithServerMessage()
    {
        await using var server = await FakeServer.StartAsync((_, _) => new FakeReply(200, [FakeReply.Delta(new JsonObject { ["content"] = "x" }), """{"error":{"message":"out of memory"}}"""]));
        var ex = await Assert.ThrowsAsync<InfraException>(() => Run(server));
        Assert.True(ex.Retryable);
        Assert.Contains("out of memory", ex.Message);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task StreamEndingWithoutCompletionIsRetryableInfra(bool sendDone)
    {
        await using var server = await FakeServer.StartAsync((_, _) => new FakeReply(200, [UsageOnly]), sendDone: sendDone);
        var ex = await Assert.ThrowsAsync<InfraException>(() => Run(server));
        Assert.True(ex.Retryable);
        Assert.Contains("without a completion", ex.Message);
    }

    private static readonly string UsageOnly = FakeReply.UsageChunk(1, 2, null);

    [Fact] public async Task MissingFinishReasonWithContentAndDoneIsStillAResponse()
    {
        await using var server = await FakeServer.StartAsync((_, _) => new FakeReply(200, [FakeReply.Delta(new JsonObject { ["content"] = "hi" })]));
        var r = await Run(server);
        Assert.Equal("hi", r.Text);
        Assert.Null(r.FinishReason);
    }

    [Fact] public async Task ContentCutOffWithoutFinishReasonOrDoneIsRetryableInfra()
    {
        await using var server = await FakeServer.StartAsync((_, _) => new FakeReply(200, [FakeReply.Delta(new JsonObject { ["content"] = "partial ans" })]), sendDone: false);
        var ex = await Assert.ThrowsAsync<InfraException>(() => Run(server));
        Assert.True(ex.Retryable);
    }

    [Fact] public async Task NonSseCompletionBodyIsParsed()
    {
        const string body = """{"choices":[{"message":{"content":"hello","tool_calls":[{"id":"c1","function":{"name":"get","arguments":"{}"}},{"id":"c2","function":{"name":"put","arguments":"{}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":3,"completion_tokens":4}}""";
        await using var server = await FakeServer.StartAsync((_, _) => new FakeReply(200, null, body));
        var r = await Run(server);
        Assert.Equal("hello", r.Text);
        Assert.Equal(["get", "put"], r.ToolCalls.Select(c => c.Name));
        Assert.Equal("tool_calls", r.FinishReason);
        Assert.Equal(new Usage(3, 4, null), r.Usage);
    }

    [Theory] [InlineData("<html>hi</html>")] [InlineData("""{"error":{"message":"bad"}}""")] [InlineData("")]
    public async Task NonSseGarbageBodyIsInfra(string body)
    {
        await using var server = await FakeServer.StartAsync((_, _) => new FakeReply(200, null, body));
        await Assert.ThrowsAsync<InfraException>(() => Run(server));
    }

    [Fact] public async Task MalformedJsonShapesDoNotThrow()
    {
        await using var server = await FakeServer.StartAsync((_, _) => new FakeReply(200,
        [
            """{"choices":null}""", """{"choices":{"a":1}}""", """{"choices":[1]}""", """{"choices":[{"delta":"x"}]}""", "[1]",
            FakeReply.Delta(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 1.5, ["id"] = "c1", ["function"] = new JsonObject { ["name"] = "get", ["arguments"] = "{}" } }, 5) }),
            FakeReply.Delta([], "tool_calls"),
            """{"choices":[],"usage":{"prompt_tokens":12.0,"completion_tokens":7.5,"completion_tokens_details":{"reasoning_tokens":"x"}}}""",
        ]));
        var r = await Run(server);
        Assert.Equal("c1", Assert.Single(r.ToolCalls).Id);
        Assert.Equal(new Usage(12, null, null), r.Usage);
    }
}
