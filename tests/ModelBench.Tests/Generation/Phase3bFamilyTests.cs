using System.Diagnostics;
using System.Text.Json;
using ModelBench.Core.Domain;
using ModelBench.Core.Execution;
using ModelBench.Core.Families;
using ModelBench.Core.Generation;
using ModelBench.Core.Sandbox;

namespace ModelBench.Tests.Generation;

public abstract class ProceduralFamilyTests(IBenchmarkFamily family, string goldenHash)
{
    protected IBenchmarkFamily Family { get; } = family;

    [Fact] public void ValidatesTwentySeedsAcrossEveryComplexity()
    {
        for (var c = 1; c <= 5; c++)
            for (long seed = 0; seed < 20; seed++)
            {
                var task = Family.Generate(new Rng(seed), c);
                var v = Family.Validate(task);
                Assert.True(v.Ok, $"c={c} seed={seed}: {string.Join("; ", v.Problems)}");
                Assert.True(task.KnownBadTurns.Count >= 2);
                Assert.Contains("<answer>", task.Prompt.User);
                Assert.Equal(AnswerFormat.Json, task.Prompt.AnswerFormat);
            }
    }

    [Fact] public void GenerationIsDeterministic()
    {
        for (var c = 1; c <= 5; c++)
        {
            var (a, b) = (Family.Generate(new Rng(5L), c), Family.Generate(new Rng(5L), c));
            Assert.Equal(JsonUtil.Serialize(a.Prompt), JsonUtil.Serialize(b.Prompt));
            Assert.Equal(JsonUtil.Serialize(a.Hidden), JsonUtil.Serialize(b.Hidden));
        }
    }

    [Fact] public void GoldenSuiteHashPinsTheGenerator()
    {
        var hash = SuiteGenerator.Generate(new SuiteSpec("golden", 7, [new FamilySpec(Family.Id, 2, 2), new FamilySpec(Family.Id, 5, 2)])).ContentHash;
        Assert.True(hash == goldenHash, $"golden hash is now {hash}");
    }

    [Fact] public async Task SelfTestPasses()
    {
        var report = await SelfTest.RunAsync(Family.Id, 4, new UnavailableSandbox(), default);
        Assert.True(report.Ok, string.Join("\n", report.Failures.Select(f => $"c{f.Complexity} {f.Case}: {f.Problem}")));
        Assert.Equal(0, report.Skipped);
    }

    [Fact] public void ValidateDetectsAReferenceTurnWithTheWrongAnswer()
    {
        var task = Family.Generate(new Rng(3L), 3);
        Assert.False(Family.Validate(task with { ReferenceTurns = [new ChatMessage(Role.Assistant, "<answer>{}</answer>")] }).Ok);
    }
}

public class ConstraintSatisfactionFamilyTests() : ProceduralFamilyTests(new ConstraintSatisfactionFamily(), "ab9e7347f8e2ab1f61ed7da5bfe325bdeb79f97c4b4ae9d6d0425cf47827260e")
{
    [Fact] public void ValidateDetectsACorruptedOptimum()
    {
        for (long seed = 0; seed < 12; seed++)
        {
            var task = Family.Generate(new Rng(seed), 2);
            var corrupted = task.Hidden with
            {
                Graders = task.Hidden.Graders.Select(g => g.Config.TryGetProperty("optimum", out var o)
                    ? g with { Config = JsonUtil.Element(new { problem = g.Config.GetProperty("problem").GetString(), instance = g.Config.GetProperty("instance"), optimum = o.GetInt32() + 1, optimalOnly = true }) }
                    : g).ToList(),
            };
            var v = Family.Validate(task with { Hidden = corrupted });
            Assert.False(v.Ok);
            Assert.Contains(v.Problems, p => p.Contains("independent"));
        }
    }

    [Fact] public void CoversEveryProblemAndScalesInstanceSize()
    {
        var problems = Enumerable.Range(0, 60).Select(s => Family.Generate(new Rng((long)s), 3).Hidden.Reference!.Value.GetProperty("problem").GetString()).Distinct().Order().ToArray();
        Assert.Equal(["coloring", "knapsack", "schedule"], problems);
        static int Items(GeneratedTask t) => t.Hidden.Graders[0].Config.GetProperty("instance") is var i
            && i.TryGetProperty("items", out var items) ? items.GetArrayLength() : -1;
        var sizes = Enumerable.Range(1, 5).Select(c => Enumerable.Range(0, 40).Select(s => Family.Generate(new Rng((long)s), c)).Where(t => Items(t) > 0).Max(Items)).ToArray();
        Assert.Equal([5, 10, 15, 20, 25], sizes);
    }
}

public class DeductiveReasoningFamilyTests(ITestOutputHelper output) : ProceduralFamilyTests(new DeductiveReasoningFamily(), "ae6ff91afed9838f006090d7341b2aac12b246b04364a6c4ad4b822c437cebc2")
{
    [Fact] public void GridSizeScalesFromThreeByThreeToFiveByFive()
    {
        for (var c = 1; c <= 5; c++)
        {
            var expected = JsonDocument.Parse(Family.Generate(new Rng(1L), c).Hidden.Graders[0].Config.GetProperty("expected").GetRawText()).RootElement;
            Assert.Equal(new[] { 3, 3, 4, 4, 5 }[c - 1], expected.EnumerateObject().Count());
            Assert.Equal(new[] { 3, 4, 4, 5, 5 }[c - 1] - 1, expected.EnumerateObject().First().Value.EnumerateObject().Count());
        }
    }

    [Fact] public void ValidateDetectsACorruptedSolution()
    {
        var task = Family.Generate(new Rng(2L), 3);
        var expected = task.Hidden.Graders[0].Config.GetProperty("expected").GetRawText();
        var first = JsonDocument.Parse(expected).RootElement.EnumerateObject().First();
        var other = JsonDocument.Parse(expected).RootElement.EnumerateObject().Skip(1).First();
        // exchange the first two people's rows: a different assignment that must break some clue
        var swapped = expected.Replace($"\"{first.Name}\":", "\"@@\":").Replace($"\"{other.Name}\":", $"\"{first.Name}\":").Replace("\"@@\":", $"\"{other.Name}\":");
        var corrupted = task.Hidden with { Graders = [task.Hidden.Graders[0] with { Config = JsonUtil.Element(new { expected = JsonDocument.Parse(swapped).RootElement.Clone() }) }] };
        Assert.False(Family.Validate(task with { Hidden = corrupted }).Ok);
    }

    [Fact] public void BruteForceValidationStaysUnderTwoSecondsAtFiveByFive()
    {
        double worst = 0;
        for (long seed = 0; seed < 20; seed++)
        {
            var task = Family.Generate(new Rng(seed), 5);
            var sw = Stopwatch.StartNew();
            Assert.True(Family.Validate(task).Ok);
            worst = Math.Max(worst, sw.Elapsed.TotalSeconds);
        }
        output.WriteLine($"worst 5x5 validate: {worst:F3}s");
        Assert.True(worst < 2, $"worst validation took {worst:F2}s");
    }
}

public class RepositoryComprehensionFamilyTests : ProceduralFamilyTests
{
    public RepositoryComprehensionFamilyTests() : base(new RepositoryComprehensionFamily(), "ce7176a4c9be634e73cbb4b740848e53e43e893dfc92b7c1c18c916b1190fa7a") { }

    [Fact] public void FileCountScalesAndAllQuestionKindsAppear()
    {
        var counts = Enumerable.Range(1, 5).Select(c => System.Text.RegularExpressions.Regex.Matches(Family.Generate(new Rng(1L), c).Prompt.User, "^### `", System.Text.RegularExpressions.RegexOptions.Multiline).Count).ToArray();
        Assert.Equal([3, 5, 8, 13, 20], counts);
        var kinds = Enumerable.Range(0, 60).Select(s => Family.Generate(new Rng((long)s), 3).Hidden.Reference!.Value.GetProperty("kind").GetString()).Distinct().Order().ToArray();
        Assert.Equal(["callers", "chain", "config", "defined-in"], kinds);
    }

    [Fact] public void ValidateDetectsACorruptedExpectedAnswer()
    {
        for (long seed = 0; seed < 12; seed++)
        {
            var task = Family.Generate(new Rng(seed), 3);
            var spec = task.Hidden.Graders[0];
            var e = spec.Config.GetProperty("expected");
            object wrong = e.ValueKind switch
            {
                JsonValueKind.Number => e.GetInt32() + 1,
                JsonValueKind.String => e.GetString() + "x",
                _ => e.EnumerateArray().Select(x => x.GetString()!).Append("zz_extra").ToArray(),
            };
            var bad = task.Hidden with { Graders = [spec with { Config = JsonUtil.Element(new { expected = wrong }) }] };
            Assert.False(Family.Validate(task with { Hidden = bad }).Ok);
        }
    }

    [Fact] public void RenderedRepositoryHidesNothingFromTheHiddenData()
    {
        var task = Family.Generate(new Rng(4L), 4);
        Assert.DoesNotContain("expected", task.Prompt.User);
        Assert.Contains("```python", task.Prompt.User);
    }
}
