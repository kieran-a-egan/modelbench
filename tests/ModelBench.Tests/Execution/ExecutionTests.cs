using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ModelBench.Core.Config;
using ModelBench.Core.Domain;
using ModelBench.Core.Execution;
using ModelBench.Core.Families;
using ModelBench.Core.Metrics;
using ModelBench.Core.Persistence;
using ModelBench.Core.Providers;
using ModelBench.Core.Sandbox;
using ModelBench.Tests.Support;

namespace ModelBench.Tests.Execution;


public class MetricCalculatorTests
{
    private static ModelResponse Resp(Usage usage, TimeSpan? ttft, TimeSpan total)
        => new("x", [], null, "stop", usage, new Timing(DateTimeOffset.UtcNow, ttft, total), "{}", "");

    private static Metric Get(IEnumerable<Metric> ms, string name) => ms.Single(m => m.Name == name);

    [Fact] public void MeasuredCalculatedAndEstimatedValuesCarryTheRightProvenance()
    {
        var ms = MetricCalculator.Compute([Resp(new Usage(1000, 600, 100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(1200))],
            TimeSpan.FromSeconds(2), new Pricing(2m, 10m, 20m), new EnergyProfile(360, 0.5m));
        Assert.Equal((1000, Provenance.Measured), (Get(ms, "input_tokens").Value, Get(ms, "input_tokens").Provenance));
        Assert.Equal(Provenance.Measured, Get(ms, "reasoning_tokens").Provenance);
        Assert.Equal((200, Provenance.Measured), (Get(ms, "ttft_ms").Value, Get(ms, "ttft_ms").Provenance));
        Assert.Equal((2000, Provenance.Measured), (Get(ms, "duration_ms").Value, Get(ms, "duration_ms").Provenance));
        Assert.Equal(1, Get(ms, "model_calls").Value);
        Assert.Equal((600, Provenance.Calculated), (Get(ms, "throughput_tps").Value, Get(ms, "throughput_tps").Provenance)); // 600 tokens / 1.0 s
        // 1000*2 + (600-100)*10 + 100*20 = 9000 per million
        Assert.Equal(0.009, Get(ms, "cost_usd").Value!.Value, 9);
        Assert.Equal(Provenance.Calculated, Get(ms, "cost_usd").Provenance);
        Assert.Equal(0.0002, Get(ms, "energy_kwh").Value!.Value, 9); // 360 W * 2 s
        Assert.Equal(0.0001, Get(ms, "energy_cost_usd").Value!.Value, 9);
        Assert.All(new[] { "energy_kwh", "energy_cost_usd" }, n => Assert.Equal(Provenance.Estimated, Get(ms, n).Provenance));
    }

    [Fact] public void EnergyBillsInferenceTimeOnlyAndSharesTheMachineAcrossConcurrentAttempts()
    {
        var r = Resp(new Usage(1, 1, null), null, TimeSpan.FromSeconds(1));
        double Kwh(int concurrency) => Get(MetricCalculator.Compute([r], TimeSpan.FromSeconds(30), null, new EnergyProfile(360, 0.5m), inferenceTime: TimeSpan.FromSeconds(2), concurrency: concurrency), "energy_kwh").Value!.Value;
        Assert.Equal(0.0002, Kwh(1), 9); // 2 s of inference; the other 28 s of back-off is not billed
        Assert.Equal(Kwh(1) / 4, Kwh(4), 12);
    }

    [Fact] public void ReasoningTokensExceedingOutputMakeCostUnavailableNotNegative()
    {
        var ms = MetricCalculator.Compute([Resp(new Usage(10, 5, 8), null, TimeSpan.FromSeconds(1))], TimeSpan.FromSeconds(1), new Pricing(1, 1), null);
        Assert.Equal(Provenance.Unavailable, Get(ms, "cost_usd").Provenance);
    }

    [Fact] public void MissingTelemetryIsUnavailableNeverEstimated()
    {
        var ms = MetricCalculator.Compute([Resp(Usage.Unknown, null, TimeSpan.FromSeconds(1))], TimeSpan.FromSeconds(1), new Pricing(1, 1), null);
        foreach (var n in new[] { "input_tokens", "output_tokens", "reasoning_tokens", "ttft_ms", "throughput_tps", "cost_usd", "energy_kwh", "energy_cost_usd" })
        {
            Assert.Equal(Provenance.Unavailable, Get(ms, n).Provenance);
            Assert.Null(Get(ms, n).Value);
        }
        Assert.Equal(Provenance.Measured, Get(ms, "duration_ms").Provenance);
    }

    [Fact] public void ZeroPriceIsAValidPriceAndMissingPricingIsUnavailable()
    {
        var r = Resp(new Usage(10, 10, null), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5));
        var free = Get(MetricCalculator.Compute([r], TimeSpan.FromSeconds(1), new Pricing(0, 0), null), "cost_usd");
        Assert.Equal((0.0, Provenance.Calculated), (free.Value, free.Provenance));
        Assert.Equal(Provenance.Unavailable, Get(MetricCalculator.Compute([r], TimeSpan.FromSeconds(1), null, null), "cost_usd").Provenance);
    }

    [Fact] public void ReasoningBilledAtOutputRateWhenNoReasoningPrice()
    {
        var r = Resp(new Usage(0, 1_000_000, 400_000), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5));
        Assert.Equal(10.0, Get(MetricCalculator.Compute([r], TimeSpan.FromSeconds(1), new Pricing(1, 10), null), "cost_usd").Value);
    }

    [Fact] public void PartialUsageAcrossCallsIsUnavailable()
    {
        var ms = MetricCalculator.Compute([Resp(new Usage(1, 1, null), null, TimeSpan.Zero), Resp(Usage.Unknown, null, TimeSpan.Zero)], TimeSpan.Zero, null, null);
        Assert.Equal(Provenance.Unavailable, Get(ms, "input_tokens").Provenance);
        Assert.Equal(2, Get(ms, "model_calls").Value);
    }
}

public class RunServiceTests
{
    [Fact] public void RequiresPersistedSuite()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        Assert.Throws<InvalidOperationException>(() => RunService.CreateRun(db, Guid.NewGuid(), TestKit.Model("http://127.0.0.1:9/v1"), ExecutionMode.SingleTurn, 1, 10, new RetryPolicy(), null));
    }

    [Fact] public void SnapshotsSettingsWithoutSecrets()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var suite = TestKit.MakeSuite(db, 1);
        var run = RunService.CreateRun(db, suite.Id, TestKit.Model("http://127.0.0.1:9/v1", apiKey: "sk-top-secret"), ExecutionMode.SingleTurn, 2, 30, new RetryPolicy(5, 10, 20), "lbl");
        Assert.DoesNotContain("sk-top-secret", run.ModelSnapshotJson);
        Assert.Contains("\"reasoningEffort\":\"low\"", run.ModelSnapshotJson);
        Assert.Contains("\"maxTokensField\":\"max_tokens\"", run.ModelSnapshotJson);
        Assert.Equal(new RetryPolicy(5, 10, 20), JsonUtil.Deserialize<RetryPolicy>(run.RetryPolicyJson));
        var env = JsonNode.Parse(run.EnvironmentJson)!;
        Assert.NotEmpty((string)env["os"]!);
        Assert.NotEmpty((string)env["dotnet"]!);
        Assert.NotEqual(Environment.MachineName, (string)env["machine"]!);
        Assert.NotNull(env["gitSha"]);
        Assert.NotNull(env["appVersion"]);
        Assert.Equal(1, db.Runs.Count());
    }

    [Fact] public void RetryPolicyDefaultsAndBackoff()
    {
        var p = new RetryPolicy();
        Assert.Equal((3, 1000, 30000), (p.MaxRetries, p.BaseDelayMs, p.MaxDelayMs));
        Assert.Equal(TimeSpan.FromSeconds(2), p.Delay(1));
        Assert.Equal(TimeSpan.FromSeconds(30), p.Delay(10));
    }
}

public class RunExecutorTests
{
    private static async Task<(Run run, List<Attempt> attempts)> Execute(BenchDb db, FakeServer server, int tasks, int repeats = 1, int timeout = 30, RetryPolicy? policy = null)
    {
        var suite = TestKit.MakeSuite(db, tasks);
        var model = TestKit.Model(server.BaseUrl);
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.SingleTurn, repeats, timeout, policy ?? new RetryPolicy(3, 1, 5), null);
        await new RunExecutor(ProviderFactory.Create(model), model, new UnavailableSandbox()).ExecuteAsync(db, run.Id, default);
        return (db.Runs.AsNoTracking().Single(r => r.Id == run.Id),
            db.Attempts.AsNoTracking().Include(a => a.Assertions).Include(a => a.Metrics).Where(a => a.RunId == run.Id).ToList());
    }

    [Fact] public async Task RetriesTransientFailuresAndGradesTheEventualAnswer()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        await using var server = await FakeServer.StartAsync((i, body) => i < 2 ? FakeReply.Error(503) : FakeReply.Text("<answer>x</answer>"));
        var (run, attempts) = await Execute(db, server, 1);
        var a = Assert.Single(attempts);
        Assert.Equal(2, a.Retries);
        Assert.Equal(3, server.Calls);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(AttemptOutcome.Failed, a.Outcome); // "x" is a well-formed but wrong answer: a model failure, never retried
    }

    [Fact] public async Task RetriedAttemptThatAnswersCorrectlyPasses()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var expected = new Dictionary<string, string>();
        await using var server = await FakeServer.StartAsync((i, body) => i < 2 ? FakeReply.Error(503) : FakeReply.Text($"<answer>{expected[TestKit.UserText(body)]}</answer>"));
        var suite = TestKit.MakeSuite(db, 1);
        foreach (var t in suite.Tasks) expected[JsonUtil.Deserialize<TaskPrompt>(t.PromptJson).User] = TestKit.Expected(t);
        var model = TestKit.Model(server.BaseUrl);
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.SingleTurn, 1, 30, new RetryPolicy(3, 1, 5), null);
        await new RunExecutor(ProviderFactory.Create(model), model, new UnavailableSandbox()).ExecuteAsync(db, run.Id, default);
        var a = db.Attempts.Single(x => x.RunId == run.Id);
        Assert.Equal((AttemptOutcome.Passed, 2, 1.0), (a.Outcome, a.Retries, a.Score));
    }

    [Fact] public async Task PersistentServerErrorIsInfraErrorExcludedFromScore()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Error(500));
        var (run, attempts) = await Execute(db, server, 2);
        Assert.All(attempts, a =>
        {
            Assert.Equal(AttemptOutcome.InfraError, a.Outcome);
            Assert.Equal(3, a.Retries);
            Assert.Null(a.Score);
            Assert.Contains("HTTP 500", a.ErrorMessage);
        });
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(8, server.Calls); // (1 + 3 retries) x 2 tasks

        using var sw = new StringWriter();
        await ModelBench.Cli.CliApp.RunAsync(["--db", dir.File("test.db"), "run", "show", run.Id.ToString()], sw, sw);
        Assert.Contains("InfraError 2", sw.ToString());
        Assert.DoesNotContain("pass rate", sw.ToString());
    }

    [Fact] public async Task NonRetryableErrorIsNotRetried()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Error(400));
        var (_, attempts) = await Execute(db, server, 1);
        Assert.Equal((AttemptOutcome.InfraError, 0), (attempts[0].Outcome, attempts[0].Retries));
        Assert.Equal(1, server.Calls);
    }

    [Fact] public async Task SlowResponseTimesOut()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        await using var server = await FakeServer.StartAsync((_, _) => new FakeReply(200, Enumerable.Repeat(FakeReply.Delta(new JsonObject { ["content"] = "." }), 400).ToList()));
        var suite = TestKit.MakeSuite(db, 1);
        var model = TestKit.Model(server.BaseUrl);
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.SingleTurn, 1, 1, new RetryPolicy(0, 1, 1), null);
        await new RunExecutor(ProviderFactory.Create(model), model, new UnavailableSandbox()).ExecuteAsync(db, run.Id, default);
        Assert.Equal(AttemptOutcome.Timeout, db.Attempts.Single(a => a.RunId == run.Id).Outcome);
    }

    [Fact] public async Task RerunSkipsFinishedAttempts()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Text("<answer>1</answer>"));
        var suite = TestKit.MakeSuite(db, 3);
        var model = TestKit.Model(server.BaseUrl);
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.SingleTurn, 2, 30, new RetryPolicy(), null);
        var executor = new RunExecutor(ProviderFactory.Create(model), model, new UnavailableSandbox());
        var progress = new List<AttemptProgress>();
        executor.AttemptCompleted += progress.Add;
        await executor.ExecuteAsync(db, run.Id, default);
        await executor.ExecuteAsync(db, run.Id, default);
        Assert.Equal(6, server.Calls);
        Assert.Equal(6, progress.Count);
        Assert.Equal(6, progress[^1].Total);
        Assert.Equal(6, db.Attempts.Count(a => a.RunId == run.Id));
    }

    [Fact] public async Task CancellationStopsAndMarksTheRunCancelledThenResumes()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Text("<answer>1</answer>"));
        var suite = TestKit.MakeSuite(db, 4);
        var model = TestKit.Model(server.BaseUrl);
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.SingleTurn, 1, 30, new RetryPolicy(), null);
        var executor = new RunExecutor(ProviderFactory.Create(model), model, new UnavailableSandbox());
        using var cts = new CancellationTokenSource();
        executor.AttemptCompleted += p => { if (p.Done == 2) cts.Cancel(); };
        await executor.ExecuteAsync(db, run.Id, cts.Token);
        Assert.Equal(RunStatus.Cancelled, db.Runs.AsNoTracking().Single(r => r.Id == run.Id).Status);
        Assert.Equal(2, db.Attempts.Count(a => a.RunId == run.Id));

        await new RunExecutor(ProviderFactory.Create(model), model, new UnavailableSandbox()).ExecuteAsync(db, run.Id, default);
        Assert.Equal(4, db.Attempts.Count(a => a.RunId == run.Id));
        Assert.Equal(RunStatus.Completed, db.Runs.AsNoTracking().Single(r => r.Id == run.Id).Status);
    }

    [Fact] public async Task AttemptStoresTranscriptRawPayloadsAssertionsAndMetrics()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Text("<answer>1</answer>", 100, 20, 5));
        var (_, attempts) = await Execute(db, server, 1);
        var a = attempts.Single();
        Assert.Contains("<answer>1</answer>", a.FinalText);
        Assert.Equal(2, JsonUtil.Deserialize<List<ChatMessage>>(a.TranscriptJson!).Count); // user + assistant (no system prompt)
        Assert.Single(JsonUtil.Deserialize<List<string>>(a.RawRequestsJson!));
        Assert.Single(JsonUtil.Deserialize<List<string>>(a.RawResponsesJson!));
        Assert.Contains(a.Assertions, x => x.Name == "answer-format");
        Assert.Equal(10, a.Metrics.Count);
    }
}

public class HiddenIsolationTests
{
    private static readonly string[] SandboxGraders = ["ast", "compile", "performance", "unit-tests", "differential"];
    private static readonly string[] Unmarked = ["language", "entryPoint", "kind", "type"]; // identifiers the graders parse

    /// <summary>Prefixes every string leaf with a unique marker. Leaves under "args" are the test inputs, the only hidden data that legitimately enters the sandbox.</summary>
    private static JsonNode? Mark(JsonNode? n, bool inArgs, string key, List<(string Marker, bool SandboxOk)> markers) => n switch
    {
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Mark(p.Value, inArgs || p.Key == "args", p.Key, markers)))),
        JsonArray a => new JsonArray(a.Select(x => Mark(x, inArgs, key, markers)).ToArray()),
        JsonValue v when v.TryGetValue<string>(out var str) && !Unmarked.Contains(key) => Leaf(str, inArgs, markers),
        _ => n?.DeepClone(),
    };

    private static JsonNode Leaf(string s, bool inArgs, List<(string, bool)> markers)
    {
        var marker = $"HIDDEN-MARKER-{markers.Count}-{Guid.NewGuid():N}";
        markers.Add((marker, inArgs));
        return marker + s;
    }

    [Fact] public async Task HiddenMarkersNeverReachTheProviderOrTheStoredRequests()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        // Every registered family at every complexity, one task each. Families whose graders use the sandbox get a run (and recording sandbox) of
        // their own, so each is shown to reach it; the rest share one run per execution mode.
        var families = FamilyRegistry.All.SelectMany(f => Enumerable.Range(Complexity.Min, Complexity.Max - Complexity.Min + 1).Select(c => (f, c))).ToList();
        var needsSandbox = families.GroupBy(x => x.f.Id).Where(g => g.Any(x => x.f.Generate(new ModelBench.Core.Generation.Rng(1), x.c, 0).Hidden.Graders.Any(gr => SandboxGraders.Contains(gr.Type)))).Select(g => g.Key).ToHashSet();
        Assert.Contains("code-generation", needsSandbox);
        var groups = families.GroupBy(x => needsSandbox.Contains(x.f.Id) ? x.f.Id : "mode-" + x.f.Mode).ToList();
        var suites = groups.ToDictionary(g => g.Key, g => ModelBench.Core.Generation.SuiteGenerator.Generate(new ModelBench.Core.Generation.SuiteSpec("iso-" + g.Key, 5,
            g.Select(x => new ModelBench.Core.Generation.FamilySpec(x.f.Id, x.c, 1)).ToList())));
        var markers = new List<(string Marker, bool SandboxOk)>();
        foreach (var t in suites.Values.SelectMany(s => s.Tasks))
        {
            // Everything the model must not see: grader configs (expected values, test inputs, file contents), the environment and the reference.
            var hidden = JsonUtil.Deserialize<TaskHidden>(t.HiddenJson);
            var reference = hidden.Reference is { } r ? JsonNode.Parse(r.GetRawText()) : null;
            var marker = $"HIDDEN-MARKER-{markers.Count}-{Guid.NewGuid():N}";
            markers.Add((marker, false));
            t.HiddenJson = JsonUtil.Serialize(hidden with
            {
                Graders = hidden.Graders.Select(g => g with { Config = JsonUtil.Element(Mark(JsonNode.Parse(g.Config.GetRawText()), false, "", markers)!) }).ToList(),
                Environment = hidden.Environment is { } e ? JsonUtil.Element(Mark(JsonNode.Parse(e.GetRawText()), false, "", markers)!) : null,
                Reference = JsonUtil.Element(new { marker, original = Mark(reference, false, "", markers) }),
            });
        }
        foreach (var s in suites.Values) ModelBench.Core.Generation.SuiteGenerator.Persist(db, s);
        Assert.All(markers, m => Assert.Contains(m.Marker, string.Concat(db.Tasks.Select(t => t.HiddenJson)))); // the markers really are stored

        // the reply carries an answer and a code block in both languages, so code graders reach the sandbox
        const string reply = "<answer>1</answer>\n```python\ndef f(x):\n    return x\n```\n```csharp\npublic static class S { public static int F(int x) => x; }\n```";
        await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Text(reply));
        var model = TestKit.Model(server.BaseUrl);
        var runs = new List<Guid>();
        var sandboxRequests = new List<string>();
        foreach (var (key, suite) in suites)
        {
            var sandbox = new RecordingSandbox(new FakeSandbox(_ => Task.FromResult(FakeSandbox.Crashed)));
            var run = RunService.CreateRun(db, suite.Id, model, groups.First(g => g.Key == key).First().f.Mode, 1, 30, new RetryPolicy(), null);
            runs.Add(run.Id);
            await new RunExecutor(ProviderFactory.Create(model), model, sandbox).ExecuteAsync(db, run.Id, default);
            if (needsSandbox.Contains(key)) Assert.True(sandbox.Requests.Count > 0, $"{key} never reached the sandbox");
            sandboxRequests.AddRange(sandbox.Requests.Select(r => DockerSandbox.BuildRequestJson(r, "n")));
        }

        // Tool/agentic turns are not exercised: a tool result legitimately returns environment data to the model.
        var sent = string.Concat(server.Bodies);
        var stored = string.Concat(db.Attempts.Where(a => runs.Contains(a.RunId)).Select(a => a.RawRequestsJson + a.TranscriptJson)) + string.Concat(db.Tasks.Select(t => t.PromptJson));
        // The sandbox legitimately receives the candidate code, entry point and the test inputs ("args"); never an expected value, reference or environment.
        var toSandbox = string.Concat(sandboxRequests);
        Assert.All(markers, m =>
        {
            Assert.DoesNotContain(m.Marker, sent);
            Assert.DoesNotContain(m.Marker, stored);
            if (!m.SandboxOk) Assert.DoesNotContain(m.Marker, toSandbox);
        });
        // nor do the expected answers appear in any request as grader config
        Assert.DoesNotContain("\"graders\"", sent + toSandbox);
    }
}

public class ManualExchangeTests
{
    private sealed record Row(Guid TaskId, int Repeat, string Response);

    [Fact] public async Task ExportImportRoundTrip()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var suite = TestKit.MakeSuite(db, 3);
        var model = ModelCatalog.Parse(TestKit.ModelsJson("http://127.0.0.1:9/v1")).Resolve("manual/chat-ui");
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.Manual, 1, 60, new RetryPolicy(), "manual");
        var outDir = dir.File("out");

        Assert.Equal(3, ManualExchange.Export(db, run.Id, outDir));
        var prompts = File.ReadAllLines(Path.Combine(outDir, "prompts.jsonl")).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        Assert.Equal(3, prompts.Count);
        Assert.All(prompts, p =>
        {
            Assert.Equal(run.Id.ToString(), (string?)p["runId"]);
            Assert.Equal(0, (int?)p["repeat"]);
            Assert.Equal("Tagged", (string?)p["answerFormat"]);
            Assert.Contains("<answer>", (string?)p["user"]);
        });
        Assert.Contains(prompts[0]["taskId"]!.GetValue<string>(), File.ReadAllText(Path.Combine(outDir, "prompts.md")));

        var tasks = db.Tasks.Where(t => t.SuiteId == suite.Id).OrderBy(t => t.Index).ToList();
        var responses = new[]
        {
            JsonUtil.Serialize(new Row(tasks[0].Id, 0, $"<think>hmm</think>done <answer>{TestKit.Expected(tasks[0])}</answer>")),
            JsonUtil.Serialize(new Row(tasks[1].Id, 0, "<answer>999999999</answer>")),
            JsonUtil.Serialize(new Row(tasks[2].Id, 0, "no tags here")),
            JsonUtil.Serialize(new Row(Guid.NewGuid(), 0, "stray")),
            "not json",
        };
        File.WriteAllLines(dir.File("responses.jsonl"), responses);

        var report = await ManualExchange.ImportAsync(db, run.Id, dir.File("responses.jsonl"), new UnavailableSandbox(), default);
        Assert.Equal(3, report.Graded);
        Assert.Equal(2, report.Errors.Count);

        var attempts = db.Attempts.Include(a => a.Metrics).Where(a => a.RunId == run.Id).ToList().OrderBy(a => tasks.FindIndex(t => t.Id == a.TaskId)).ToList();
        Assert.Equal([AttemptOutcome.Passed, AttemptOutcome.Failed, AttemptOutcome.Failed], attempts.Select(a => a.Outcome));
        Assert.All(attempts, a =>
        {
            Assert.Equal(10, a.Metrics.Count);
            Assert.All(a.Metrics, m => Assert.Equal((Provenance.Unavailable, (double?)null), (m.Provenance, m.Value)));
        });
        Assert.Equal(RunStatus.Completed, db.Runs.Single(r => r.Id == run.Id).Status);

        var again = await ManualExchange.ImportAsync(db, run.Id, dir.File("responses.jsonl"), new UnavailableSandbox(), default);
        Assert.Equal(0, again.Graded); // duplicates are reported, not re-graded
    }

    [Fact] public async Task ImportIsRefusedForARunGradedWithAnotherMethodologyAndChangesNothing()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var suite = TestKit.MakeSuite(db, 2);
        var model = ModelCatalog.Parse(TestKit.ModelsJson("http://127.0.0.1:9/v1")).Resolve("manual/chat-ui");
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.Manual, 1, 60, new RetryPolicy(), null);
        run.GradingVersion = 1;
        db.SaveChanges();
        var task = db.Tasks.First(t => t.SuiteId == suite.Id);
        File.WriteAllLines(dir.File("r.jsonl"), [JsonUtil.Serialize(new Row(task.Id, 0, "<answer>1</answer>"))]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ManualExchange.ImportAsync(db, run.Id, dir.File("r.jsonl"), new UnavailableSandbox(), default));
        Assert.Contains("would mix them", ex.Message);
        Assert.Empty(db.Attempts.AsNoTracking().Where(a => a.RunId == run.Id));
        Assert.Equal(RunStatus.Pending, db.Runs.AsNoTracking().Single(r => r.Id == run.Id).Status);
    }

    [Fact] public async Task NullLineIsReportedAsAMalformedLineAndTheImportContinues()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var suite = TestKit.MakeSuite(db, 2);
        var model = ModelCatalog.Parse(TestKit.ModelsJson("http://127.0.0.1:9/v1")).Resolve("manual/chat-ui");
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.Manual, 1, 60, new RetryPolicy(), "manual");
        var task = db.Tasks.First(t => t.SuiteId == suite.Id);
        File.WriteAllLines(dir.File("r.jsonl"), ["null", JsonUtil.Serialize(new Row(task.Id, 0, $"<answer>{TestKit.Expected(task)}</answer>"))]);

        var report = await ManualExchange.ImportAsync(db, run.Id, dir.File("r.jsonl"), new UnavailableSandbox(), default);
        Assert.Equal(1, report.Graded);
        Assert.Single(report.Errors);
        Assert.StartsWith("line 1:", report.Errors[0]);
    }

    [Fact] public void ExportRejectsNonManualRuns()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var suite = TestKit.MakeSuite(db, 1);
        var run = RunService.CreateRun(db, suite.Id, TestKit.Model("http://127.0.0.1:9/v1"), ExecutionMode.SingleTurn, 1, 5, new RetryPolicy(), null);
        Assert.Throws<InvalidOperationException>(() => ManualExchange.Export(db, run.Id, dir.File("o")));
    }
}
