using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ModelBench.Core.Domain;
using ModelBench.Core.Execution;
using ModelBench.Core.Families;
using ModelBench.Core.Generation;
using ModelBench.Core.Providers;
using ModelBench.Core.Sandbox;
using ModelBench.Tests.Support;

namespace ModelBench.Tests.Generation;

public class ToolCallingFamilyTests
{
    private static readonly IBenchmarkFamily Family = FamilyRegistry.Get("tool-calling");

    [Fact] public void ValidatesThirtySeedsAcrossEveryComplexity()
    {
        for (var c = 1; c <= 5; c++)
            for (long seed = 0; seed < 30; seed++)
            {
                var task = Family.Generate(new Rng(seed), c);
                var v = Family.Validate(task);
                Assert.True(v.Ok, $"c={c} seed={seed}: {string.Join("; ", v.Problems)}");
                Assert.Contains("<answer>", task.Prompt.User);
                Assert.Equal(5, task.Prompt.Tools.Count);
                Assert.Equal(AnswerFormat.Tagged, task.Prompt.AnswerFormat);
                Assert.True(task.KnownBadTurns.Count >= 2);
            }
    }

    [Fact] public void ComplexityScalesTheNumberOfRequiredCalls()
    {
        int Required(int c) => Family.Generate(new Rng(1L), c).Hidden.Graders[0].Config.GetProperty("required").GetArrayLength();
        Assert.Equal([1, 1, 2, 3, 5], Enumerable.Range(1, 5).Select(Required)); // calculate is never required
        Assert.Equal(14, Family.Generate(new Rng(1L), 5).Hidden.Graders[0].Config.GetProperty("maxCalls").GetInt32()); // 2 * 6 + 2
    }

    [Fact] public void GenerationIsDeterministicAndPinned()
    {
        for (var c = 1; c <= 5; c++)
        {
            var (a, b) = (Family.Generate(new Rng(5L), c), Family.Generate(new Rng(5L), c));
            Assert.Equal(JsonUtil.Serialize(a.Prompt), JsonUtil.Serialize(b.Prompt));
            Assert.Equal(JsonUtil.Serialize(a.Hidden), JsonUtil.Serialize(b.Hidden));
        }
        Assert.Equal("e895b2a2140e2c82b72b13a46ad7cfdd3febde0115348af9a3b09d637c0d0465", SuiteGenerator.Generate(new SuiteSpec("golden", 7, Enumerable.Range(1, 5).Select(c => new FamilySpec("tool-calling", c, 1)).ToList())).ContentHash);
    }

    [Fact] public void PromptNeverContainsTheTables()
    {
        for (var c = 1; c <= 5; c++)
        {
            var task = Family.Generate(new Rng(c), c);
            var prompt = JsonUtil.Serialize(task.Prompt);
            var data = JsonUtil.Deserialize<ToolData>(task.Hidden.Environment!.Value.GetRawText());
            Assert.All(data.Customers.Skip(2), x => Assert.DoesNotContain(x.Name, prompt));
            Assert.All(data.Orders.Take(20), o => Assert.DoesNotContain(o.Id, prompt));
        }
    }

    [Fact] public void ValidateDetectsTampering()
    {
        foreach (var c in new[] { 1, 2, 3, 5 })
        {
            var task = Family.Generate(new Rng(3L), c);
            var answer = task.Hidden.Graders[1];
            var corruptExpected = answer with { Config = JsonUtil.Element(new { expected = "12345", absTol = "0.01", normalize = "trim" }) };
            Assert.False(Family.Validate(task with { Hidden = task.Hidden with { Graders = [task.Hidden.Graders[0], corruptExpected] } }).Ok, $"expected c={c}");

            // Change the data instead of the expected value: the LINQ recomputation no longer agrees.
            var data = JsonUtil.Deserialize<ToolData>(task.Hidden.Environment!.Value.GetRawText());
            var tampered = data with
            {
                Rates = [.. data.Rates.Select(r => r with { Value = r.Value + 0.5m })],
                Stock = [.. data.Stock.Select(s => s with { Qty = s.Qty + 1 })],
                Customers = [.. data.Customers.Select(x => x with { City = "Nowhere" })],
            };
            Assert.False(Family.Validate(task with { Hidden = task.Hidden with { Environment = JsonUtil.Element(tampered) } }).Ok, $"env c={c}");

            // A required call pointing at a record that does not exist.
            var cfg = JsonNode.Parse(task.Hidden.Graders[0].Config.GetRawText())!;
            var first = cfg["required"]![0]!;
            first["args"] = new JsonObject { ["customer_id"] = "C-0000", ["sku"] = "SKU-0000", ["from"] = "XXX", ["to"] = "YYY" };
            var badSpec = task.Hidden.Graders[0] with { Config = JsonDocument.Parse(cfg.ToJsonString()).RootElement.Clone() };
            Assert.False(Family.Validate(task with { Hidden = task.Hidden with { Graders = [badSpec, task.Hidden.Graders[1]] } }).Ok, $"args c={c}");
        }
    }

    [Fact] public async Task CorrectAnswerWithoutTheCalculatorStillPasses()
    {
        for (var c = 1; c <= 5; c++)
        {
            var task = Family.Generate(new Rng(4L), c);
            var turns = task.ReferenceTurns.Where(t => t.ToolCalls is null || t.ToolCalls.All(x => x.Name != "calculate")).ToList();
            Assert.True(turns.Count < task.ReferenceTurns.Count == (c >= 2));
            var ep = await EpisodeRunner.RunAsync(ExecutionMode.ToolAssisted, task.Prompt, task.Hidden, Family, new ScriptedProvider(turns), default);
            var graded = await ModelBench.Core.Grading.GradingPipeline.GradeAsync(task.Prompt, task.Hidden, ep.Transcript, ep.FinalEnvironmentState, new UnavailableSandbox(), default);
            Assert.True(graded.Passed && graded.Score == 1.0, $"c={c}: {string.Join("; ", graded.Assertions.Where(a => !a.Passed).Select(a => a.Name + " " + a.Diagnostic))}");
        }
    }

    [Fact] public async Task SelfTestPasses()
    {
        var report = await SelfTest.RunAsync("tool-calling", 5, new UnavailableSandbox(), default);
        Assert.True(report.Ok, string.Join("\n", report.Failures.Select(x => $"{x.Family} c{x.Complexity} {x.Case}: {x.Problem}")));
        Assert.True(report.Checks >= 5 * 5 * 4);
    }

    [Theory]
    [InlineData("1 + 2 * 3", "7")]
    [InlineData("(1 + 2) * 3", "9")]
    [InlineData("10 / 4", "2.5")]
    [InlineData("-(2 + 3) * -2", "10")]
    [InlineData("12.50 + 0.25", "12.75")]
    [InlineData("  2 *  (3 + (4 - 1)) ", "12")]
    public void CalculateEvaluatesDecimalArithmetic(string expr, string expected)
    {
        var env = new TableToolEnvironment(new ToolData([], [], [], []));
        using var doc = JsonDocument.Parse(env.Execute(new ToolCall("c", "calculate", JsonUtil.Serialize(new { expression = expr }))));
        Assert.Equal(decimal.Parse(expected), doc.RootElement.GetProperty("result").GetDecimal());
    }

    [Theory]
    [InlineData("1 / 0")]
    [InlineData("2 +")]
    [InlineData("(1 + 2")]
    [InlineData("abc")]
    [InlineData("__import__('os')")]
    [InlineData("1 2")]
    [InlineData("2 ** 3")]
    public void CalculateRejectsEverythingElseWithAnErrorPayload(string expr)
    {
        var env = new TableToolEnvironment(new ToolData([], [], [], []));
        var result = env.Execute(new ToolCall("c", "calculate", JsonUtil.Serialize(new { expression = expr })));
        Assert.Contains("\"error\"", result);
    }

    [Fact] public void CalculateBoundsInputSize()
    {
        var env = new TableToolEnvironment(new ToolData([], [], [], []));
        Assert.Contains("\"error\"", env.Execute(new ToolCall("c", "calculate", JsonUtil.Serialize(new { expression = new string('(', 5000) }))));
        Assert.Contains("\"error\"", env.Execute(new ToolCall("c", "calculate", JsonUtil.Serialize(new { expression = new string('(', 200) + "1" + new string(')', 200) }))));
    }

    [Fact] public void ToolsReturnErrorPayloadsForBadInput()
    {
        var task = Family.Generate(new Rng(2L), 3);
        var env = Family.CreateEnvironment(task.Hidden)!;
        string Run(string name, string args) => env.Execute(new ToolCall("c", name, args));
        Assert.Contains("unknown customer", Run("lookup_customer", """{"customer_id":"C-0"}"""));
        Assert.Contains("unknown customer", Run("list_orders", """{"customer_id":"C-0"}"""));
        Assert.Contains("unknown sku", Run("get_stock", """{"sku":"nope"}"""));
        Assert.Contains("no rate", Run("get_exchange_rate", """{"from":"USD","to":"ZZZ"}"""));
        Assert.Contains("missing", Run("get_stock", "{}"));
        Assert.Contains("missing", Run("get_stock", """{"sku":5}"""));
        Assert.Contains("not valid JSON", Run("get_stock", "{nope"));
        Assert.Contains("unknown tool", Run("rm_rf", "{}"));
        var state = env.State;
        Assert.Equal(8, state.GetProperty("calls").GetInt32());
        Assert.All(state.GetProperty("log").EnumerateArray(), e => Assert.False(e.GetProperty("ok").GetBoolean()));
    }

    // ---- a run against the fake OpenAI-compatible server ----

    private static FakeReply Reply(ChatMessage turn) => turn.ToolCalls is { Count: > 0 } calls
        ?
        new(200,
        [
            FakeReply.Delta(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["id"] = calls[0].Id, ["function"] = new JsonObject { ["name"] = calls[0].Name, ["arguments"] = calls[0].ArgumentsJson } }) }),
            FakeReply.Delta([], "tool_calls"),
            FakeReply.UsageChunk(10, 5, null),
        ])
        : FakeReply.Text(turn.Content!);

    /// <summary>Plays the reference turns, choosing the turn by how many assistant messages the request already holds.</summary>
    private static Func<int, JsonObject, FakeReply> ReferenceServer(GeneratedTask task, List<string>? seen = null) => (_, body) =>
    {
        seen?.Add(body.ToJsonString());
        return Reply(task.ReferenceTurns[body["messages"]!.AsArray().Count(m => (string?)m!["role"] == "assistant")]);
    };

    [Fact] public async Task ToolAssistedRunAgainstTheFakeServerPasses()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var spec = new SuiteSpec("tools", 11, [new FamilySpec("tool-calling", 3, 1), new FamilySpec("tool-calling", 5, 1)]);
        var suite = SuiteGenerator.Generate(spec);
        SuiteGenerator.Persist(db, suite);
        var tasks = suite.Tasks.ToDictionary(t => JsonUtil.Deserialize<TaskPrompt>(t.PromptJson).User, t => Family.Generate(new Rng(t.Seed), t.Complexity));
        await using var server = await FakeServer.StartAsync((i, body) =>
        {
            var user = body["messages"]!.AsArray().Last(m => (string?)m!["role"] == "user")!["content"]!.GetValue<string>();
            return ReferenceServer(tasks[user])(i, body);
        });
        var model = TestKit.Model(server.BaseUrl);
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.ToolAssisted, 1, 60, new RetryPolicy(0, 1, 1), null);
        await new RunExecutor(ProviderFactory.Create(model), model, new UnavailableSandbox()).ExecuteAsync(db, run.Id, default);

        var attempts = db.Attempts.AsNoTracking().Include(a => a.Assertions).Include(a => a.Metrics).Where(a => a.RunId == run.Id).OrderBy(a => a.TaskId).ToList();
        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, a => Assert.True(a.Outcome == AttemptOutcome.Passed && a.Score == 1.0, $"{a.Outcome} {a.Score}: {string.Join("; ", a.Assertions.Where(x => !x.Passed).Select(x => x.Name + " " + x.Diagnostic))}"));
        var calls = attempts.Select(a => a.Metrics.Single(m => m.Name == "model_calls").Value).Order().ToList();
        Assert.Equal([4.0, 7.0], calls); // 3 tool calls + answer, 6 tool calls + answer
        Assert.All(attempts, a => Assert.Contains("\"role\":\"Tool\"", a.TranscriptJson));
    }

    [Fact] public async Task HiddenDataNeverReachesTheProviderExceptThroughToolResults()
    {
        const string Marker = "ZZ-HIDDEN-MARKER-7731";
        var task = Family.Generate(new Rng(9L), 4);
        // A field the tools never project: it exists only in the hidden tables.
        var env = JsonNode.Parse(task.Hidden.Environment!.Value.GetRawText())!;
        foreach (var customer in env["customers"]!.AsArray()) customer!["internalNote"] = Marker;
        var hidden = task.Hidden with { Environment = JsonDocument.Parse(env.ToJsonString()).RootElement.Clone() };

        var seen = new List<string>();
        await using var server = await FakeServer.StartAsync(ReferenceServer(task, seen));
        var ep = await EpisodeRunner.RunAsync(ExecutionMode.ToolAssisted, task.Prompt, hidden, Family, new OpenAiCompletionsProvider(TestKit.Model(server.BaseUrl)), default);

        Assert.Equal(task.ReferenceTurns.Count, seen.Count);
        Assert.All(seen, body =>
        {
            Assert.DoesNotContain(Marker, body);
            Assert.DoesNotContain("maxCalls", body);    // grader config
            Assert.DoesNotContain("\"expected\"", body);
        });
        Assert.Contains("\"city\"", (string?)JsonNode.Parse(seen[1])!["messages"]!.AsArray().Last()!["content"]);            // what a tool legitimately returned does show up later
        Assert.DoesNotContain(Marker, JsonUtil.Serialize(ep.Transcript));
        Assert.DoesNotContain(Marker, string.Join("", ep.Responses.Select(r => r.RawRequest)));
    }
}
