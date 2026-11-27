using System.Diagnostics;
using ModelBench.Core.Domain;
using ModelBench.Core.Sandbox;

namespace ModelBench.Core.Grading;

/// <summary>
/// Config: { language, entryPoint, args: [...], expected: json, budgetMs }. One sandbox Run of a single input.
/// Assertions: "correct" (output equals expected) and "within-budget".
/// Budget semantics (ADR 0006): the budget is the HOST wall time around ExecuteAsync, so it includes container start-up
/// (and compilation, for C#). The sandbox gets TimeoutMs = budgetMs, but DockerSandbox adds a 5 s start-up grace before it kills,
/// so a slow run can finish after the budget without being killed; within-budget therefore requires both "not TimedOut" and
/// measured wall time &lt;= budgetMs, and a correct output. In-container elapsedMs is forgeable by candidate code and appears only in the diagnostic.
/// Wall time depends on host load, so the verdict is empirical, not reproducible (MB-002): families emit this grader with
/// Weight 0 and Required false, and GradingPipeline forces that for every performance spec whatever its stored Weight/Required, so it is reported but never moves Score or Passed, and Regrader skips every unscored assertion (Weight 0, not Required), including correct.
/// </summary>
public sealed class PerformanceGrader : IGrader
{
    public string Type => "performance";

    public async Task<IReadOnlyList<AssertionOutcome>> GradeAsync(GraderSpec spec, GradingContext ctx)
    {
        var expected = spec.Config.GetProperty("expected");
        var budget = spec.Config.GetProperty("budgetMs").GetInt32();
        var budgetText = $"<= {budget} ms";
        if (ctx.Answer is null)
            return [AssertionOutcome.Check("correct", false, expected.GetRawText(), null, "no answer"), AssertionOutcome.Check("within-budget", false, budgetText, null, "no answer")];

        var req = new SandboxRequest(CodeSupport.Language(spec.Config), SandboxAction.Run, ctx.Answer,
            spec.Config.GetProperty("entryPoint").GetString()!, [spec.Config.GetProperty("args")], budget);
        var sw = Stopwatch.StartNew();
        var r = await ctx.Sandbox.ExecuteAsync(req, ctx.CancellationToken);
        var wall = sw.ElapsedMilliseconds;

        var why = CodeSupport.Unusable(r, 1);
        var c = why is null ? r.Cases[0] : null;
        var correct = c is { Error: null } && CodeSupport.JsonEqual(c.Output ?? CodeSupport.Null, expected);
        var inContainer = c is null ? "" : $" (in-container {c.ElapsedMs:0.#} ms, informational)";
        var within = correct && !r.TimedOut && wall <= budget; // MB-003: a fast wrong answer earns nothing
        var verdict = within ? "within budget" : r.TimedOut ? "killed at budget" : !correct ? "incorrect" : "over budget";
        return
        [
            AssertionOutcome.Check("correct", correct, expected.GetRawText(), c is null ? null : CodeSupport.Actual(c),
                why ?? (c!.Error is { } e ? "error: " + e : $"expected {expected.GetRawText()} but got {CodeSupport.Actual(c)}")),
            // Actual is the verdict only, so re-grading is reproducible; the noisy measurement goes in the diagnostic.
            new AssertionOutcome("within-budget", budgetText, verdict, within ? 1 : 0, within,
                r.TimedOut ? $"killed by the host after {wall} ms; budget {budget} ms"
                    : !correct ? $"no performance credit for an incorrect result (host wall time {wall} ms)"
                    : $"host wall time {wall} ms {(wall <= budget ? "within" : "exceeds")} the {budget} ms budget{inContainer}"),
        ];
    }
}
