using System.Text.Json;
using System.Text.Json.Nodes;
using ModelBench.Core.Domain;
using ModelBench.Core.Execution;
using ModelBench.Core.Families;
using ModelBench.Core.Generation;
using ModelBench.Core.Grading;
using ModelBench.Core.Providers;
using ModelBench.Core.Sandbox;
using ModelBench.Tests.Sandbox;
using ModelBench.Tests.Support;

namespace ModelBench.Tests.Generation;

public class CodeFamilyTests
{
    public static IEnumerable<object[]> Families() => [["code-generation"], ["debugging"]];

    private static GeneratedTask Gen(string family, int c, long seed) => FamilyRegistry.Get(family).Generate(new Rng(seed), c);

    [Theory, MemberData(nameof(Families))]
    public void ValidatesManySeedsAtEveryComplexity(string family)
    {
        var f = FamilyRegistry.Get(family);
        for (var c = 1; c <= 5; c++)
            for (long seed = 0; seed < 40; seed++)
            {
                var v = f.Validate(Gen(family, c, seed));
                Assert.True(v.Ok, $"{family} c{c} seed {seed}: {string.Join("; ", v.Problems)}");
            }
    }

    [Theory, MemberData(nameof(Families))]
    public void GenerationIsDeterministic(string family)
    {
        for (var c = 1; c <= 5; c++)
        {
            var (a, b) = (Gen(family, c, 5), Gen(family, c, 5));
            Assert.Equal(JsonUtil.Serialize(a.Prompt), JsonUtil.Serialize(b.Prompt));
            Assert.Equal(JsonUtil.Serialize(a.Hidden), JsonUtil.Serialize(b.Hidden));
        }
    }

    [Theory, MemberData(nameof(Families))]
    public void KnownBadTurnsAndGraderSetFollowComplexity(string family)
    {
        for (var c = 1; c <= 5; c++)
        {
            var t = Gen(family, c, 3);
            Assert.True(t.KnownBadTurns.Count >= 2);
            Assert.Equal(AnswerFormat.CodeBlock, t.Prompt.AnswerFormat);
            var types = t.Hidden.Graders.Select(g => g.Type).ToList();
            Assert.Contains("compile", types);
            Assert.Contains("unit-tests", types);
            Assert.Contains("differential", types);
            var random = t.Hidden.Graders.Single(g => g.Type == "differential").Config.GetProperty("cases").GetArrayLength();
            Assert.Equal(5 + 6 * (c - 1), random);
            if (family == "code-generation")
            {
                Assert.Equal(c >= 3, types.Contains("performance"));
                Assert.Equal(c >= 4, types.Contains("ast"));
            }
        }
    }

    [Fact] public void CodeGenerationCatalogueHasEightTasksAndBothLanguages()
    {
        var ids = Enumerable.Range(0, 400).Select(s => Gen("code-generation", s % 2 + 1, s).Hidden.Reference!.Value.GetProperty("task").GetString()).Distinct().ToList();
        Assert.True(ids.Count >= 8, string.Join(",", ids));
        var langs = Enumerable.Range(0, 100).Select(s => Gen("code-generation", 2, s).Hidden.Graders[0].Config.GetProperty("language").GetString()).Distinct();
        Assert.Equal(2, langs.Count());
    }

    [Fact] public void ValidateCatchesATamperedExpectedValue()
    {
        var t = Gen("code-generation", 2, 1);
        var json = JsonUtil.Serialize(t.Hidden).Replace("\"expected\":", "\"expected\":-1,\"was\":");
        var tampered = t with { Hidden = JsonUtil.Deserialize<TaskHidden>(json) };
        Assert.False(FamilyRegistry.Get("code-generation").Validate(tampered).Ok);
    }

    [Fact] public void DebuggingValidateCatchesAnUnchangedFunctionAndAWrongExample()
    {
        var f = FamilyRegistry.Get("debugging");
        var t = Gen("debugging", 3, 2);
        Assert.False(f.Validate(t with { Prompt = t.Prompt with { User = "no code here" } }).Ok);
        var noSites = JsonNode.Parse(t.Hidden.Reference!.Value.GetRawText())!.AsObject();
        noSites["sites"] = new JsonArray();
        var hidden = t.Hidden with { Reference = JsonSerializer.SerializeToElement(noSites) };
        Assert.False(f.Validate(t with { Hidden = hidden }).Ok);
    }

    [Fact] public void DebuggingPromptShowsBuggyCodeAndAFailingExampleThatIsNotHidden()
    {
        for (var c = 1; c <= 5; c++)
        {
            var t = Gen("debugging", c, 9);
            var buggy = Assert.IsType<string>(t.KnownBadTurns[0][0].Content);
            var code = buggy[(buggy.IndexOf("```python\n") + 10)..buggy.LastIndexOf("\n```")];
            Assert.Contains(code, t.Prompt.User);
            Assert.NotEqual(code, t.ReferenceTurns[0].Content![(t.ReferenceTurns[0].Content!.IndexOf("```python\n") + 10)..t.ReferenceTurns[0].Content!.LastIndexOf("\n```")]);
            Assert.Contains("should return", t.Prompt.User);
        }
    }

    // ---------------------------------------------------------------- hidden isolation (ADR 0002)

    [Theory, MemberData(nameof(Families))]
    public async Task HiddenMarkersNeverReachThePromptOrTheSandbox(string family)
    {
        var real = await SandboxEnv.Require();
        const string marker = "ZZ-HIDDEN-MARKER-7731";
        for (var c = 1; c <= 5; c += 2)
        {
            var t = Gen(family, c, 4);
            var marked = t.Hidden with
            {
                Graders = t.Hidden.Graders.Select(g =>
                {
                    var o = JsonNode.Parse(g.Config.GetRawText())!.AsObject();
                    o["marker"] = marker;
                    return g with { Config = JsonSerializer.SerializeToElement(o) };
                }).ToList(),
                Reference = JsonSerializer.SerializeToElement(new { marker }),
            };
            Assert.DoesNotContain(marker, JsonUtil.Serialize(t.Prompt));

            var recorder = new RecordingSandbox(real);
            foreach (var turns in t.KnownBadTurns.Prepend(t.ReferenceTurns))
            {
                var ep = await EpisodeRunner.RunSingleTurnAsync(t.Prompt, new ScriptedProvider(turns), default);
                Assert.DoesNotContain(marker, JsonUtil.Serialize(ep.Transcript.Where(m => m.Role == Role.User)));
                await GradingPipeline.GradeAsync(t.Prompt, marked, ep.Transcript, null, recorder, default);
            }
            Assert.NotEmpty(recorder.Requests);
            foreach (var req in recorder.Requests)
            {
                var json = DockerSandbox.BuildRequestJson(req, "n");
                Assert.DoesNotContain(marker, json);
                // Expected values are structurally absent: the wire request has only these fields.
                Assert.Equal(["action", "code", "entryPoint", "inputs", "nonce", "repetitions"], JsonNode.Parse(json)!.AsObject().Select(p => p.Key).Order());
            }
        }
    }
}

public class CodeGenerationSelfTest
{
    [Fact] public async Task ReferenceAndKnownBadAnswersBehaveInTheRealSandbox()
        => await SelfTestCases.Check("code-generation", 2);
}

public class DebuggingSelfTest
{
    [Fact] public async Task ReferenceAndKnownBadAnswersBehaveInTheRealSandbox()
        => await SelfTestCases.Check("debugging", 3);
}

internal static class SelfTestCases
{
    public static async Task Check(string family, int samples)
    {
        var sandbox = await SandboxEnv.Require();
        var report = await SelfTest.RunAsync(family, samples, sandbox, default);
        Assert.True(report.Ok, string.Join("\n", report.Failures.Select(f => $"{f.Family} c{f.Complexity} #{f.Sample} {f.Case}: {f.Problem}")));
        Assert.True(report.Checks >= 5 * samples * 4);
    }
}
