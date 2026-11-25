using ModelBench.Core.Domain;
using ModelBench.Core.Execution;
using ModelBench.Core.Families;
using ModelBench.Core.Generation;
using ModelBench.Core.Sandbox;

namespace ModelBench.Tests.Phase3a;

public class Phase3aFamilyTests
{
    public static TheoryData<string> Ids => new() { "instruction-fidelity", "data-transformation", "policy-evaluation", "long-context-retrieval" };

    [Theory, MemberData(nameof(Ids))]
    public void ValidatesThirtySeedsAcrossEveryComplexity(string id)
    {
        var f = FamilyRegistry.Get(id);
        Assert.Equal(id, f.Category);
        for (var c = 1; c <= 5; c++)
            for (long seed = 0; seed < 30; seed++)
            {
                var task = f.Generate(new Rng(seed), c);
                var v = f.Validate(task);
                Assert.True(v.Ok, $"{id} c={c} seed={seed}: {string.Join("; ", v.Problems)}");
                Assert.True(task.KnownBadTurns.Count >= 2);
                Assert.Contains("<answer>", task.Prompt.User);
            }
    }

    [Theory, MemberData(nameof(Ids))]
    public void GenerationIsDeterministic(string id)
    {
        var f = FamilyRegistry.Get(id);
        for (var c = 1; c <= 5; c++)
        {
            var (a, b) = (f.Generate(new Rng(5L), c), f.Generate(new Rng(5L), c));
            Assert.Equal(JsonUtil.Serialize(a.Prompt), JsonUtil.Serialize(b.Prompt));
            Assert.Equal(JsonUtil.Serialize(a.Hidden), JsonUtil.Serialize(b.Hidden));
        }
    }

    private static GraderSpec Corrupt(GraderSpec g) => g.Type switch
    {
        "exact" => g with { Config = JsonUtil.Element(new { expected = "ZZ-0000", normalize = "trim" }) },
        "structural" when g.Config.TryGetProperty("key", out var k) =>
            g with { Config = JsonUtil.Element(new { expected = g.Config.GetProperty("expected").GetString() == "allow" ? "deny" : "allow", key = k.GetString() }) },
        "structural" => g with { Config = JsonUtil.Element(new { expected = new[] { new { nope = 1 } } }) },
        "text-constraints" => g with { Config = JsonUtil.Element(new { constraints = g.Config.GetProperty("constraints").EnumerateArray().Append(JsonUtil.Element(new { kind = "contains", text = "@@never@@" })) }) },
        _ => g,
    };

    [Theory, MemberData(nameof(Ids))]
    public void ValidateDetectsACorruptedExpectedValue(string id)
    {
        var f = FamilyRegistry.Get(id);
        var task = f.Generate(new Rng(3L), 3);
        var corrupted = task.Hidden with { Graders = [.. task.Hidden.Graders.Select(Corrupt)] };
        Assert.False(f.Validate(task with { Hidden = corrupted }).Ok);
    }

    [Theory, MemberData(nameof(Ids))]
    public async Task SelfTestPasses(string id)
    {
        var report = await SelfTest.RunAsync(id, 5, new UnavailableSandbox(), default);
        Assert.True(report.Ok, string.Join("\n", report.Failures.Select(x => $"{x.Family} c{x.Complexity} {x.Case}: {x.Problem}")));
    }

    [Fact] public void ComplexityScalesTheWork()
    {
        int Len(string id, int c) => FamilyRegistry.Get(id).Generate(new Rng(1L), c).Prompt.User.Length;
        foreach (var id in new[] { "data-transformation", "long-context-retrieval" })
            Assert.True(Len(id, 5) > 3 * Len(id, 1), id);
        Assert.Equal(10, FamilyRegistry.Get("instruction-fidelity").Generate(new Rng(1L), 5).Hidden.Graders[0].Config.GetProperty("constraints").GetArrayLength());
        Assert.Equal(2, FamilyRegistry.Get("instruction-fidelity").Generate(new Rng(1L), 1).Hidden.Graders[0].Config.GetProperty("constraints").GetArrayLength());
    }
}
