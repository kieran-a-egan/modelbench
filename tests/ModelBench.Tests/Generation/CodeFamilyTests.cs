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

    [Fact] public void CapacityIsExplicitAndExactlyThatManyDistinctPromptsGenerateReproducibly()
    {
        // #4 / MB-006: one visible prompt per task variant at a complexity; beyond the capacity is a clear error, never a silent repeat.
        var fam = FamilyRegistry.Get("code-generation");
        Assert.Equal([12, 22, 10, 10, 10], Enumerable.Range(1, 5).Select(c => fam.Capacity(c)!.Value));
        for (var c = 1; c <= 5; c++)
        {
            var capacity = fam.Capacity(c)!.Value;
            var a = SuiteGenerator.Generate(new SuiteSpec("full", 3, [new FamilySpec("code-generation", c, capacity)]));
            var b = SuiteGenerator.Generate(new SuiteSpec("full", 3, [new FamilySpec("code-generation", c, capacity)]));
            Assert.Equal(capacity, a.Tasks.Select(t => t.PromptJson).Distinct().Count());
            Assert.Equal(a.ContentHash, b.ContentHash);
            var ex = Assert.Throws<InvalidOperationException>(() => SuiteGenerator.Generate(new SuiteSpec("big", 3, [new FamilySpec("code-generation", c, capacity + 1)])));
            Assert.Contains($"has {capacity} distinct prompts (its capacity)", ex.Message);
        }
    }

    [Fact] public void ComplexitiesThatSharePromptsShareCapacityAcrossEntries()
    {
        // Each entry passes the per-entry capacity check (12 and 22), but complexity 1's prompts are a subset of complexity 2's.
        var ex = Assert.Throws<InvalidOperationException>(() => SuiteGenerator.Generate(new SuiteSpec("shared", 3,
            [new FamilySpec("code-generation", 1, 12), new FamilySpec("code-generation", 2, 22)])));
        Assert.Contains("already used by an earlier entry of the same family", ex.Message);
    }

    [Fact] public void EveryTaskVariantIsReachedAndValidAtEveryComplexityItServes()
    {
        Assert.Equal(CodeTasks.All.Count, CodeTasks.All.Select(t => t.Id).Distinct().Count());
        var fam = FamilyRegistry.Get("code-generation");
        for (var c = 1; c <= 5; c++)
        {
            var want = CodeGenerationFamily.Pool(c).Select(t => t.Id).ToHashSet();
            var seen = new HashSet<string>();
            for (long seed = 0; seen.Count < want.Count && seed < 2000; seed++)
            {
                var t = Gen("code-generation", c, seed);
                var v = fam.Validate(t);
                Assert.True(v.Ok, $"c{c} seed {seed}: {string.Join("; ", v.Problems)}");
                seen.Add(t.Hidden.Reference!.Value.GetProperty("task").GetString()!);
            }
            Assert.Equal(want.Order(), seen.Order());
        }
        Assert.Equal(["csharp", "python"], CodeGenerationFamily.Pool(2).Select(t => t.Lang).Distinct().Order());
    }

    [Fact] public void GoldenSuiteHashPinsTheGenerator()
    {
        var hash = SuiteGenerator.Generate(new SuiteSpec("golden", 7, Enumerable.Range(1, 5).Select(c => new FamilySpec("code-generation", c, 2)).ToList())).ContentHash;
        Assert.True(hash == "bc2e466c73816d4b75648ea77423d119f8ab3353d95edeeed5aa3017a23faf15", $"golden hash is now {hash}");
    }

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
                Assert.Equal(["action", "code", "entryPoint", "inputs", "nonce", "repetitions", "timeoutMs"], JsonNode.Parse(json)!.AsObject().Select(p => p.Key).Order());
            }
        }
    }
}

public class PerformanceBudgetCalibrationTests
{
    [Fact] public async Task EveryBudgetSeparatesTheReferenceFromTheQuadraticAnswer()
    {
        // Performance is reported, not scored (MB-002), so the quadratic answer passes the task; the report must still tell them apart.
        var sb = await SandboxEnv.Require();
        var perfTasks = CodeTasks.All.Where(t => t.Perf is not null).ToList();
        Assert.NotEmpty(perfTasks);
        foreach (var t in perfTasks)
        {
            var args = t.Perf!.Gen(new Rng(1));
            var cfg = JsonUtil.Element(new { language = t.Lang, entryPoint = t.Entry, args, expected = t.Solve(CodeKit.Elems(args)), budgetMs = t.Perf.BudgetMs });
            async Task<IReadOnlyList<AssertionOutcome>> Grade(string code) => await GraderRegistry.Get("performance").GradeAsync(new GraderSpec("performance", "p", cfg),
                new GradingContext(TaskPrompt.Simple("q", AnswerFormat.CodeBlock), new TaskHidden([]), code, code, [], null, sb, default));
            // Host wall time includes `docker run` start-up, which varies under load: the reference gets best of three, the quadratic answer (slower under load too) one.
            var fast = await Grade(t.Ref);
            for (var i = 1; i < 3 && !fast.All(o => o.Passed); i++) fast = await Grade(t.Ref);
            Assert.True(fast.All(o => o.Passed), $"{t.Id} reference: {fast[1].Diagnostic}");
            var slow = await Grade(t.Perf.Quadratic); // usually killed at the budget, so its correctness is not observable here
            Assert.False(slow[1].Passed, $"{t.Id} quadratic should be reported over budget: {slow[1].Diagnostic}");
        }
    }
}

public class CodeTaskVariantSandboxTests
{
    [Fact] public async Task EveryReferencePassesAndEveryWrongAnswerFailsItsOwnExamplesAndEdgeCases()
    {
        // Seeded sampling (selftest) may skip a variant; this covers each one's hand-written answers in the real sandbox.
        var sb = await SandboxEnv.Require();
        foreach (var t in CodeTasks.All)
        {
            var cases = t.Examples.Concat(t.Edge).Select(a => new { args = a, expected = t.Solve(CodeKit.Elems(a)) });
            var cfg = JsonUtil.Element(new { language = t.Lang, entryPoint = t.Entry, cases });
            async Task<IReadOnlyList<AssertionOutcome>> Grade(string code) => await GraderRegistry.Get("unit-tests").GradeAsync(new GraderSpec("unit-tests", "u", cfg),
                new GradingContext(TaskPrompt.Simple("q", AnswerFormat.CodeBlock), new TaskHidden([]), code, code, [], null, sb, default));
            var good = await Grade(t.Ref);
            Assert.True(good.All(o => o.Passed), $"{t.Id} reference: {good.FirstOrDefault(o => !o.Passed)?.Diagnostic}");
            Assert.Contains(await Grade(t.Wrong), o => !o.Passed);
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
