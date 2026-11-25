using Microsoft.EntityFrameworkCore;
using ModelBench.Core.Domain;
using ModelBench.Core.Execution;
using ModelBench.Core.Families;
using ModelBench.Core.Generation;
using ModelBench.Core.Grading;
using ModelBench.Core.Sandbox;
using ModelBench.Tests.Support;

namespace ModelBench.Tests.Generation;


public class RngTests
{
    [Fact] public void SameSeedSameSequence()
    {
        var (a, b) = (new Rng(123L), new Rng(123L));
        for (int i = 0; i < 100; i++) Assert.Equal(a.NextULong(), b.NextULong());
    }

    [Fact] public void DifferentSeedsDiffer() => Assert.NotEqual(new Rng(1L).NextULong(), new Rng(2L).NextULong());

    [Fact] public void GoldenFirstFiveForSeed42()
    {
        var r = new Rng(42UL);
        ulong[] expected = [1546998764402558742UL, 6990951692964543102UL, 12544586762248559009UL, 17057574109182124193UL, 18295552978065317476UL];
        Assert.Equal(expected, Enumerable.Range(0, 5).Select(_ => r.NextULong()).ToArray());
    }

    [Fact] public void NextStaysInBoundsAndCoversTheRange()
    {
        var r = new Rng(7L);
        var seen = new bool[10];
        for (int i = 0; i < 2000; i++) { var v = r.Next(10); Assert.InRange(v, 0, 9); seen[v] = true; }
        Assert.All(seen, Assert.True);
        for (int i = 0; i < 500; i++) Assert.InRange(r.Range(-3, 3), -3, 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => r.Next(0));
    }

    [Fact] public void DeriveSeedIsStableAndSensitiveToEveryInput()
    {
        var s = Rng.DeriveSeed(1, "mathematics", 1, 0);
        Assert.Equal(s, Rng.DeriveSeed(1, "mathematics", 1, 0));
        Assert.NotEqual(s, Rng.DeriveSeed(2, "mathematics", 1, 0));
        Assert.NotEqual(s, Rng.DeriveSeed(1, "other", 1, 0));
        Assert.NotEqual(s, Rng.DeriveSeed(1, "mathematics", 2, 0));
        Assert.NotEqual(s, Rng.DeriveSeed(1, "mathematics", 1, 1));
    }
}

public class MathematicsFamilyTests
{
    private static readonly MathematicsFamily Family = new();

    [Fact] public void ValidatesFiftySeedsAcrossEveryComplexity()
    {
        for (int c = 1; c <= 5; c++)
            for (long seed = 0; seed < 50; seed++)
            {
                var task = Family.Generate(new Rng(seed), c);
                var v = Family.Validate(task);
                Assert.True(v.Ok, $"c={c} seed={seed}: {string.Join("; ", v.Problems)}");
                Assert.True(task.KnownBadTurns.Count >= 2);
                Assert.Contains("p/q in lowest terms", task.Prompt.User);
                Assert.Contains("<answer>", task.Prompt.User);
            }
    }

    [Fact] public void GenerationIsDeterministic()
    {
        var a = Family.Generate(new Rng(5L), 3);
        var b = Family.Generate(new Rng(5L), 3);
        Assert.Equal(JsonUtil.Serialize(a.Prompt), JsonUtil.Serialize(b.Prompt));
        Assert.Equal(JsonUtil.Serialize(a.Hidden), JsonUtil.Serialize(b.Hidden));
    }

    [Fact] public void ExercisesEveryProblemKind()
    {
        var kinds = Enumerable.Range(0, 200).Select(s => JsonUtil.Serialize(Family.Generate(new Rng((long)s), 3).Hidden))
            .SelectMany(h => new[] { "expr", "modpow", "lincong", "choose", "derange", "lattice" }.Where(k => h.Contains($"\"kind\":\"{k}\"")))
            .Distinct().Count();
        Assert.Equal(6, kinds);
    }

    [Fact] public void ValidateDetectsCorruptedExpectedValue()
    {
        var task = Family.Generate(new Rng(3L), 2);
        var corrupted = task.Hidden with
        {
            Graders = task.Hidden.Graders.Select(g => g.Type == "numeric" ? g with { Config = JsonUtil.Element(new { expected = "123456789" }) } : g).ToList(),
        };
        var v = Family.Validate(task with { Hidden = corrupted });
        Assert.False(v.Ok);
        Assert.Contains(v.Problems, p => p.Contains("independent"));
    }

    [Fact] public void ValidateDetectsAReferenceTurnWithTheWrongAnswer()
    {
        var task = Family.Generate(new Rng(3L), 2);
        Assert.False(Family.Validate(task with { ReferenceTurns = [new ChatMessage(Role.Assistant, "<answer>0</answer>")] }).Ok);
    }
}

public class ReproducibilityTests
{
    private static SuiteSpec Spec(long seed) => new("s", seed, [new FamilySpec("mathematics", 1, 3), new FamilySpec("mathematics", 4, 3)]);

    [Fact] public void SameSpecSameHashes()
    {
        var (a, b) = (SuiteGenerator.Generate(Spec(11)), SuiteGenerator.Generate(Spec(11)));
        Assert.Equal(a.ContentHash, b.ContentHash);
        Assert.Equal(a.Tasks.Select(t => t.ContentHash), b.Tasks.Select(t => t.ContentHash));
        Assert.Equal(a.SpecJson, b.SpecJson);
    }

    [Fact] public void DifferentSeedDifferentHash()
        => Assert.NotEqual(SuiteGenerator.Generate(Spec(11)).ContentHash, SuiteGenerator.Generate(Spec(12)).ContentHash);

    [Fact] public void HashesFollowTheSpecifiedFormula()
    {
        var suite = SuiteGenerator.Generate(Spec(11));
        var t = suite.Tasks[0];
        Assert.Equal(SuiteGenerator.TaskHash(t.PromptJson, t.HiddenJson), t.ContentHash);
        Assert.Equal(64, t.ContentHash.Length);
        Assert.Equal(t.ContentHash, t.ContentHash.ToLowerInvariant());
        Assert.Equal(SuiteGenerator.SuiteHash(suite.Tasks.Select(x => x.ContentHash)), suite.ContentHash);
        Assert.Equal(Rng.DeriveSeed(11, "mathematics", new MathematicsFamily().Version, 0), t.Seed);
        Assert.Contains($"\"version\":{new MathematicsFamily().Version}", suite.SpecJson);
    }

    [Fact] public void ProceduralPromptsAreDistinctWithinASuite()
    {
        var suite = SuiteGenerator.Generate(new SuiteSpec("dups", 5, [new FamilySpec("mathematics", 1, 20)]));
        Assert.Equal(20, suite.Tasks.Select(t => t.PromptJson).Distinct().Count());
    }

    [Fact] public void GoldenSuiteHashPinsTheGenerator()
        => Assert.Equal("3b241c961cb3c2ff9c851fe5344846e486e296ef3f70fb374d21767a1b61daf1", SuiteGenerator.Generate(new SuiteSpec("golden", 7, [new FamilySpec("mathematics", 2, 2), new FamilySpec("mathematics", 5, 2)])).ContentHash);

    [Fact] public void VerifyPassesOnAPersistedSuiteAndFailsAfterTampering()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var suite = SuiteGenerator.Generate(Spec(21));
        SuiteGenerator.Persist(db, suite);
        Assert.Empty(SuiteGenerator.Verify(db, suite.Id));

        var task = db.Tasks.First(t => t.SuiteId == suite.Id && t.Index == 2);
        task.PromptJson = task.PromptJson.Replace("Solve", "Sneak");
        db.SaveChanges();
        var problems = SuiteGenerator.Verify(db, suite.Id);
        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.Contains("task 2"));
    }

    [Fact] public void UnknownFamilyAndBadComplexityAreRejected()
    {
        Assert.Throws<ArgumentException>(() => SuiteGenerator.Generate(new SuiteSpec("x", 1, [new FamilySpec("nope", 1, 1)])));
        Assert.Throws<ArgumentException>(() => SuiteGenerator.Generate(new SuiteSpec("x", 1, [new FamilySpec("mathematics", 6, 1)])));
    }
}

public class SelfTestTests
{
    [Fact] public async Task MathematicsReferenceAndKnownBadAnswersBehave()
    {
        var report = await SelfTest.RunAsync("mathematics", 5, new UnavailableSandbox(), default);
        Assert.True(report.Ok, string.Join("\n", report.Failures.Select(f => $"{f.Family} c{f.Complexity} {f.Case}: {f.Problem}")));
        Assert.True(report.Checks > 100);
    }

    [Fact] public async Task UnknownFamilyThrows()
        => await Assert.ThrowsAsync<ArgumentException>(() => SelfTest.RunAsync("nope", 1, new UnavailableSandbox(), default));
}
