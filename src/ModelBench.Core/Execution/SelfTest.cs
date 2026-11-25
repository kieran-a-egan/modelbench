using ModelBench.Core.Domain;
using ModelBench.Core.Families;
using ModelBench.Core.Generation;
using ModelBench.Core.Grading;
using ModelBench.Core.Providers;
using ModelBench.Core.Sandbox;

namespace ModelBench.Core.Execution;

public sealed record SelfTestFailure(string Family, int Complexity, int Sample, string Case, string Problem);

public sealed record SelfTestReport(int Checks, IReadOnlyList<SelfTestFailure> Failures, int Skipped = 0)
{
    public bool Ok => Failures.Count == 0;
}

/// <summary>
/// Proves each family's grading is sound: reference turns must score 1.0 and pass, every known-bad turn must not pass.
/// Uses the real EpisodeRunner and GradingPipeline with a ScriptedProvider.
/// </summary>
public static class SelfTest
{
    public const long Seed = 20240607;

    public static Task<SelfTestReport> RunAsync(string? familyId, int samplesPerComplexity, ISandbox sandbox, CancellationToken ct)
        => RunAsync(familyId is null ? FamilyRegistry.All : [FamilyRegistry.Get(familyId)], samplesPerComplexity, sandbox, ct);

    public static async Task<SelfTestReport> RunAsync(IReadOnlyList<IBenchmarkFamily> families, int samplesPerComplexity, ISandbox sandbox, CancellationToken ct)
    {
        var failures = new List<SelfTestFailure>();
        var checks = 0;
        var skipped = 0;
        foreach (var fam in families)
        {
            if (fam.Mode != ExecutionMode.SingleTurn) throw new NotSupportedException($"selftest of {fam.Mode} family '{fam.Id}' is not implemented yet");
            for (var c = Complexity.Min; c <= Complexity.Max; c++)
                for (var i = 0; i < samplesPerComplexity; i++)
                {
                    void Fail(string kase, string problem) => failures.Add(new SelfTestFailure(fam.Id, c, i, kase, problem));
                    var task = fam.Generate(new Rng(Rng.DeriveSeed(Seed, fam.Id, fam.Version, c * 1000 + i)), c, i);
                    checks++;
                    var v = fam.Validate(task);
                    if (!v.Ok) Fail("validate", string.Join("; ", v.Problems));
                    if (task.ReferenceTurns.Count == 0) { skipped++; continue; } // file-defined task without a reference answer

                    var reference = await GradeAsync(task, task.ReferenceTurns, sandbox, ct);
                    checks++;
                    if (reference.Score != 1.0 || !reference.Passed)
                        Fail("reference", $"score {reference.Score}, passed {reference.Passed}: {Describe(reference)}");

                    for (var b = 0; b < task.KnownBadTurns.Count; b++)
                    {
                        var bad = await GradeAsync(task, task.KnownBadTurns[b], sandbox, ct);
                        checks++;
                        if (bad.Passed) Fail($"known-bad[{b}]", "a known-bad answer passed");
                    }
                }
        }
        return new SelfTestReport(checks, failures, skipped);
    }

    private static async Task<GradingResult> GradeAsync(GeneratedTask task, IReadOnlyList<ChatMessage> turns, ISandbox sandbox, CancellationToken ct)
    {
        var episode = await EpisodeRunner.RunSingleTurnAsync(task.Prompt, new ScriptedProvider(turns), ct);
        return await GradingPipeline.GradeAsync(task.Prompt, task.Hidden, episode.Transcript, null, sandbox, ct);
    }

    private static string Describe(GradingResult r)
        => string.Join("; ", r.Assertions.Where(a => !a.Passed).Select(a => $"{a.Name}: {a.Diagnostic}"));
}
