using System.Text.Json;
using ModelBench.Core.Domain;
using ModelBench.Core.Execution;
using ModelBench.Core.Families;
using ModelBench.Core.Generation;
using ModelBench.Core.Grading;
using ModelBench.Core.Metrics;
using ModelBench.Core.Providers;
using ModelBench.Core.Sandbox;

namespace ModelBench.Tests.Execution;

public class ToolLoopTests
{
    private sealed class FakeEnv : ISimulatedEnvironment
    {
        public List<ToolCall> Calls { get; } = [];
        public string Execute(ToolCall call) { Calls.Add(call); return call.Name == "boom" ? """{"error":"no such thing"}""" : $"ok:{call.Name}"; }
        public JsonElement State => JsonUtil.Element(new { calls = Calls.Count });
    }

    /// <summary>Records the request of every call, then replays scripted turns.</summary>
    private sealed class Recording(IEnumerable<ChatMessage> turns) : IModelProvider
    {
        private readonly ScriptedProvider _inner = new(turns);
        public List<ModelRequest> Requests { get; } = [];
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct) { Requests.Add(request); return _inner.CompleteAsync(request, ct); }
    }

    private static ChatMessage Call(string name, string args = "{}", string id = "c", string? state = null)
        => new(Role.Assistant, null, [new ToolCall(id, name, args)], ProviderState: state);

    private static readonly TaskPrompt Prompt = new("sys", "question", AnswerFormat.Tagged,
        [new ToolDefinition("t", "d", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone())]);

    [Fact] public async Task LoopsUntilTheModelAnswersWithoutToolCalls()
    {
        var env = new FakeEnv();
        var provider = new Recording([Call("a", id: "c1", state: "[{\"state\":1}]"), Call("b", id: "c2"), new ChatMessage(Role.Assistant, "<answer>1</answer>")]);
        var ep = await EpisodeRunner.RunToolLoopAsync(Prompt, env, provider, 8, default);

        Assert.Equal(["a", "b"], env.Calls.Select(c => c.Name));
        Assert.Equal(3, ep.Responses.Count);
        Assert.Equal([Role.System, Role.User, Role.Assistant, Role.Tool, Role.Assistant, Role.Tool, Role.Assistant], ep.Transcript.Select(m => m.Role));
        Assert.Equal(("c1", "a", "ok:a"), (ep.Transcript[3].ToolCallId, ep.Transcript[3].ToolName, ep.Transcript[3].Content));
        Assert.Equal("[{\"state\":1}]", ep.Transcript[2].ProviderState); // provider state rides on the assistant message
        // The model sees prompt-only messages plus the growing conversation, and the tools every time.
        Assert.Equal([2, 4, 6], provider.Requests.Select(r => r.Messages.Count));
        Assert.All(provider.Requests, r => Assert.Equal(["t"], r.Tools.Select(t => t.Name)));
        Assert.Equal(2, ep.FinalEnvironmentState!.Value.GetProperty("calls").GetInt32());
        Assert.Equal(3, MetricCalculator.Compute(ep.Responses, ep.WallTime, null, null).Single(m => m.Name == "model_calls").Value);
    }

    [Fact] public async Task StepLimitStopsTheLoopAndIsReportedAsInformational()
    {
        var env = new FakeEnv();
        var provider = new Recording(Enumerable.Range(0, 10).Select(_ => Call("a")));
        var ep = await EpisodeRunner.RunToolLoopAsync(Prompt, env, provider, 3, default);
        Assert.Equal(3, ep.Responses.Count);
        Assert.Equal(2, env.Calls.Count); // the pending calls of the last turn are not executed
        Assert.Equal(Role.Assistant, ep.Transcript[^1].Role);

        var hidden = new TaskHidden([]);
        var graded = await GradingPipeline.GradeAsync(Prompt, hidden, ep.Transcript, ep.FinalEnvironmentState, new UnavailableSandbox(), default);
        var limit = graded.Assertions.Single(a => a.Name == "step-limit");
        Assert.Equal((0.0, false, false), (limit.Weight, limit.Required, limit.Passed));
    }

    [Fact] public async Task NoStepLimitAssertionWhenTheModelFinishes()
    {
        var ep = await EpisodeRunner.RunToolLoopAsync(Prompt, new FakeEnv(), new Recording([Call("a"), new ChatMessage(Role.Assistant, "<answer>1</answer>")]), 8, default);
        var graded = await GradingPipeline.GradeAsync(Prompt, new TaskHidden([]), ep.Transcript, ep.FinalEnvironmentState, new UnavailableSandbox(), default);
        Assert.DoesNotContain(graded.Assertions, a => a.Name == "step-limit");
        Assert.Equal("1", graded.Answer);
    }

    [Fact] public async Task AnswerTextFromBeforeTheLimitIsStillTheFinalText()
    {
        var turns = new[] { new ChatMessage(Role.Assistant, "<answer>7</answer>", [new ToolCall("c", "a", "{}")]), Call("a") };
        var ep = await EpisodeRunner.RunToolLoopAsync(Prompt, new FakeEnv(), new Recording(turns), 2, default);
        var graded = await GradingPipeline.GradeAsync(Prompt, new TaskHidden([]), ep.Transcript, null, new UnavailableSandbox(), default);
        Assert.Equal("7", graded.Answer);
    }

    [Fact] public async Task EnvironmentErrorsAreSeenByTheModelAndDoNotStopTheLoop()
    {
        var provider = new Recording([Call("boom"), new ChatMessage(Role.Assistant, "done")]);
        var ep = await EpisodeRunner.RunToolLoopAsync(Prompt, new FakeEnv(), provider, 8, default);
        Assert.Equal("""{"error":"no such thing"}""", provider.Requests[1].Messages[^1].Content);
        Assert.Equal("done", ep.Transcript[^1].Content);
    }

    [Fact] public async Task OversizedArgumentsAreRejectedWithoutReachingTheEnvironment()
    {
        var env = new FakeEnv();
        var big = "{\"x\":\"" + new string('a', EpisodeRunner.MaxToolArgsBytes) + "\"}";
        var provider = new Recording([Call("a", big), Call("a", "{\"x\":1}"), new ChatMessage(Role.Assistant, "done")]);
        await EpisodeRunner.RunToolLoopAsync(Prompt, env, provider, 8, default);
        Assert.Equal(["{\"x\":1}"], env.Calls.Select(c => c.ArgumentsJson));
        Assert.Contains("64 KB", provider.Requests[1].Messages[^1].Content);
    }

    [Fact] public async Task ModesUseTheirStepLimits()
    {
        Assert.Equal((8, 30), (EpisodeRunner.ToolAssistedMaxCalls, EpisodeRunner.AgenticMaxCalls));
        var fam = FamilyRegistry.Get("tool-calling");
        var task = fam.Generate(new Rng(1L), 1);
        var always = new Recording(Enumerable.Range(0, 40).Select(_ => Call("get_stock", "{\"sku\":\"x\"}")));
        var ep = await EpisodeRunner.RunAsync(ExecutionMode.ToolAssisted, task.Prompt, task.Hidden, fam, always, default);
        Assert.Equal(8, ep.Responses.Count);
        always = new Recording(Enumerable.Range(0, 40).Select(_ => Call("get_stock", "{\"sku\":\"x\"}")));
        ep = await EpisodeRunner.RunAsync(ExecutionMode.Agentic, task.Prompt, task.Hidden, fam, always, default);
        Assert.Equal(30, ep.Responses.Count);
    }

    [Fact] public async Task ToolModesNeedAnEnvironment()
    {
        var fam = FamilyRegistry.Get("mathematics");
        var task = fam.Generate(new Rng(1L), 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => EpisodeRunner.RunAsync(ExecutionMode.ToolAssisted, task.Prompt, task.Hidden, fam, new ScriptedProvider([]), default));
    }
}
