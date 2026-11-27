using Microsoft.EntityFrameworkCore;
using ModelBench.Cli;
using ModelBench.Core.Domain;
using ModelBench.Tests.Support;

namespace ModelBench.Tests.EndToEnd;


public class EndToEndTests
{
    private static async Task<(int code, string output)> Cli(params string[] args)
    {
        var sw = new StringWriter();
        var code = await CliApp.RunAsync(args, sw, sw);
        return (code, sw.ToString());
    }

    [Fact] public async Task GenerateRunAndInspectFailuresThroughTheCli()
    {
        using var dir = new TempDir();
        var answers = new Dictionary<string, (string correct, bool answerCorrectly)>();
        await using var server = await FakeServer.StartAsync((_, body) =>
        {
            var (correct, ok) = answers[TestKit.UserText(body)];
            return FakeReply.Text($"Working...\n<answer>{(ok ? correct : "-1/7")}</answer>", prompt: 100, completion: 20, reasoning: 5);
        });
        File.WriteAllText(dir.File("models.json"), TestKit.ModelsJson(server.BaseUrl));
        string[] g = ["--db", dir.File("e2e.db"), "--models", dir.File("models.json")];

        var (code, output) = await Cli([.. g, "models", "list"]);
        Assert.Equal(0, code);
        Assert.Contains("fake/m1", output);

        (code, output) = await Cli([.. g, "suite", "generate", "--name", "e2e", "--seed", "5", "--family", "mathematics:all:2"]);
        Assert.Equal(0, code);
        var suiteShort = output.Split(' ')[1][..8];

        (code, output) = await Cli([.. g, "suite", "list"]);
        Assert.Contains(suiteShort, output);
        (code, output) = await Cli([.. g, "suite", "show", suiteShort]);
        Assert.Equal(0, code);
        Assert.Contains("mathematics", output);
        (code, output) = await Cli([.. g, "suite", "verify", suiteShort]);
        Assert.Equal(0, code);
        Assert.Contains("verified", output);

        using (var db = BenchDbOpen(dir))
            foreach (var t in db.Tasks.OrderBy(t => t.Index).ToList())
                answers[JsonUtil.Deserialize<TaskPrompt>(t.PromptJson).User] = (TestKit.Expected(t), t.Index % 2 == 0);

        (code, output) = await Cli([.. g, "run", "start", "--suite", suiteShort, "--model", "fake/m1@low", "--label", "e2e run"]);
        Assert.Equal(0, code);
        Assert.Contains("[10/10]", output);
        Assert.Contains("pass rate 5/10", output);
        var runShort = output.Split(' ')[1][..8];

        using (var db = BenchDbOpen(dir))
        {
            var attempts = db.Attempts.Include(a => a.Assertions).Include(a => a.Metrics).Include(a => a.Task).ToList();
            Assert.Equal(10, attempts.Count);
            Assert.Equal(5, attempts.Count(a => a.Outcome == AttemptOutcome.Passed));
            var failed = attempts.Where(a => a.Outcome == AttemptOutcome.Failed).ToList();
            Assert.Equal(5, failed.Count);
            Assert.All(failed, a =>
            {
                Assert.Contains(a.Assertions, x => x.Name == "value" && !x.Passed && x.Actual == "-1/7");
                Assert.True(a.Score < 1);
            });
            Assert.All(attempts.Where(a => a.Outcome == AttemptOutcome.Passed), a => Assert.Equal(1.0, a.Score));
            Assert.Equal(attempts.Count, attempts.Count(a => a.Task!.Index % 2 == 0 == (a.Outcome == AttemptOutcome.Passed)));

            var m = attempts[0].Metrics.ToDictionary(x => x.Name);
            Assert.Equal((100.0, Provenance.Measured), (m["input_tokens"].Value, m["input_tokens"].Provenance));
            Assert.Equal((20.0, Provenance.Measured), (m["output_tokens"].Value, m["output_tokens"].Provenance));
            Assert.Equal((5.0, Provenance.Measured), (m["reasoning_tokens"].Value, m["reasoning_tokens"].Provenance));
            Assert.Equal(Provenance.Measured, m["ttft_ms"].Provenance);
            Assert.Equal(Provenance.Measured, m["duration_ms"].Provenance);
            Assert.Equal(Provenance.Calculated, m["throughput_tps"].Provenance);
            Assert.Equal(Provenance.Calculated, m["cost_usd"].Provenance);
            Assert.Equal(0.00014, m["cost_usd"].Value!.Value, 9); // (100*1 + 15*2 + 5*2) / 1e6
            Assert.Equal(Provenance.Estimated, m["energy_kwh"].Provenance);
            Assert.Equal(Provenance.Estimated, m["energy_cost_usd"].Provenance);

            var run = db.Runs.Single();
            Assert.Equal(RunStatus.Completed, run.Status);
            Assert.Equal("e2e run", run.Label);
            Assert.Contains("\"reasoningEffort\":\"low\"", run.ModelSnapshotJson);
        }

        (code, output) = await Cli([.. g, "run", "list"]);
        Assert.Contains("5/10", output);
        Assert.Contains("e2e run", output);

        (code, output) = await Cli([.. g, "run", "show", runShort]);
        Assert.Contains("mathematics", output);
        Assert.DoesNotContain("FAIL ", output);

        (code, output) = await Cli([.. g, "run", "show", runShort, "--failures"]);
        Assert.Equal(0, code);
        Assert.Equal(5, output.Split('\n').Count(l => l.TrimStart().StartsWith("FAIL value:")));
        Assert.Contains("actual [-1/7]", output);
        Assert.Contains("expected [", output);
        Assert.Contains("difference", output);
    }

    [Fact] public async Task ManualFlowThroughTheCli()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("models.json"), TestKit.ModelsJson("http://127.0.0.1:9/v1"));
        string[] g = ["--db", dir.File("m.db"), "--models", dir.File("models.json")];
        var (_, output) = await Cli([.. g, "suite", "generate", "--name", "m", "--seed", "1", "--family", "mathematics:1:2"]);
        var suiteShort = output.Split(' ')[1][..8];

        var (code, exportOut) = await Cli([.. g, "manual", "export", "--suite", suiteShort, "--model", "manual/chat-ui", "--out", dir.File("out")]);
        Assert.Equal(0, code);
        var runShort = exportOut.Split(' ')[1][..8];
        Assert.True(File.Exists(dir.File("out/prompts.md")));

        var rows = File.ReadAllLines(dir.File("out/prompts.jsonl")).Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement).ToList();
        File.WriteAllLines(dir.File("r.jsonl"), rows.Select(r => JsonUtil.Serialize(new { taskId = r.GetProperty("taskId").GetString(), repeat = 0, response = "<answer>0</answer>" })));
        (code, output) = await Cli([.. g, "manual", "import", "--run", runShort, "--file", dir.File("r.jsonl")]);
        Assert.Equal(0, code);
        Assert.Contains("graded 2 responses", output);
    }

    [Fact] public async Task SelfTestCommandPasses()
    {
        var (code, output) = await Cli("selftest", "--family", "mathematics");
        Assert.Equal(0, code);
        Assert.Contains("selftest passed", output);
        (code, _) = await Cli("selftest", "--family", "nope");
        Assert.Equal(1, code);
    }

    [Fact] public async Task ErrorsAreReportedWithNonZeroExit()
    {
        using var dir = new TempDir();
        var (code, output) = await Cli("--db", dir.File("x.db"), "suite", "show", "deadbeef");
        Assert.Equal(1, code);
        Assert.Contains("error:", output);
    }

    [Fact] public void FamilySpecParsing()
    {
        var all = Parse("mathematics");
        Assert.Equal(5, all.Count);
        Assert.Equal(5, all[0].Count);
        Assert.Equal([(3, 7)], Parse("mathematics:3:7").Select(f => (f.Complexity, f.Count)));
        Assert.Equal(5, Parse("mathematics:all:2").Count);
        Assert.Throws<ArgumentException>(() => Parse("nope:1"));
    }

    [Fact] public void StaticFamiliesGetOneEntryAndMalformedSpecsAreRejected()
    {
        foreach (var id in ModelBench.Core.Families.FamilyRegistry.All.Select(f => f.Id).Where(i => i.StartsWith("static:") || i.StartsWith("repo:")))
            Assert.Single(Parse(id));
        Assert.Throws<ArgumentException>(() => Parse("mathematics::3"));
        Assert.Throws<ArgumentException>(() => Parse("mathematics:"));
    }

    [Fact] public async Task CliErrorsAreReportedForBadModelsFilesEnergyExportPathsAndRefusedRuns()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("bad.json"), "{ not json");
        var (code, output) = await Cli("--models", dir.File("bad.json"), "models", "list");
        Assert.Equal(1, code);
        Assert.StartsWith("error:", output);

        File.WriteAllText(dir.File("models.json"), TestKit.ModelsJson("http://127.0.0.1:9/v1").Replace("\"watts\":300,", ""));
        string[] g = ["--db", dir.File("e.db"), "--models", dir.File("models.json")];
        (_, output) = await Cli([.. g, "suite", "generate", "--name", "s", "--seed", "1", "--family", "mathematics:1:2"]);
        var suiteShort = output.Split(' ')[1][..8];
        (code, output) = await Cli([.. g, "run", "start", "--suite", suiteShort, "--model", "fake/m1"]);
        Assert.Equal(1, code);
        Assert.Contains("'fake/m1'", output);
        Assert.Contains("energy.watts", output);

        File.WriteAllText(dir.File("models.json"), TestKit.ModelsJson("http://127.0.0.1:9/v1"));
        (code, output) = await Cli([.. g, "run", "start", "--suite", suiteShort, "--model", "manual/chat-ui"]);
        Assert.Equal(1, code);
        (code, _) = await Cli([.. g, "run", "start", "--suite", suiteShort, "--model", "fake/m1", "--mode", "Manual"]);
        Assert.Equal(1, code);
        using (var db = BenchDbOpen(dir)) Assert.Empty(db.Runs);

        var blocker = dir.File("file.txt");
        File.WriteAllText(blocker, "x");
        (code, output) = await Cli([.. g, "manual", "export", "--suite", suiteShort, "--model", "manual/chat-ui", "--out", Path.Combine(blocker, "sub")]);
        Assert.Equal(1, code);
        Assert.Contains("error:", output);
    }

    private static List<ModelBench.Core.Generation.FamilySpec> Parse(string text)
        => ModelBench.Cli.Commands.SuiteCommands.ParseFamily(text).ToList();

    private static ModelBench.Core.Persistence.BenchDb BenchDbOpen(TempDir dir) => ModelBench.Core.Persistence.BenchDb.Open(dir.File("e2e.db"));
}
