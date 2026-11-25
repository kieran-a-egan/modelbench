using System.Text.Json;
using ModelBench.Core.Domain;
using ModelBench.Core.Families;

namespace ModelBench.Core.Grading;

/// <summary>
/// Config: { expected: "p/q" | number, absTol?: number, relTol?: number }.
/// With no tolerance the comparison is exact rational equality; otherwise |a-e| &lt;= max(absTol, relTol*|e|).
/// </summary>
public sealed class NumericGrader : IGrader
{
    public string Type => "numeric";

    public Task<IReadOnlyList<AssertionOutcome>> GradeAsync(GraderSpec spec, GradingContext ctx)
    {
        var expectedText = Raw(spec.Config.GetProperty("expected"));
        if (!Rational.TryParse(expectedText, out var expected))
            throw new ArgumentException($"grader '{spec.Name}': expected '{expectedText}' is not a number");
        var abs = Tol(spec.Config, "absTol");
        var rel = Tol(spec.Config, "relTol");

        AssertionOutcome o;
        if (ctx.Answer is null) o = AssertionOutcome.Check(spec.Name, false, expectedText, null, "no answer");
        else if (!Rational.TryParse(ctx.Answer, out var actual))
            o = AssertionOutcome.Check(spec.Name, false, expectedText, ctx.Answer, $"\"{ctx.Answer}\" is not a number (use p/q, an integer or a decimal)");
        else
        {
            var diff = (actual - expected).Abs();
            bool ok;
            if (abs is null && rel is null) ok = actual == expected;
            else
            {
                var tol = abs ?? Rational.Of(0);
                var relTol = (rel ?? Rational.Of(0)) * expected.Abs();
                if (relTol.CompareTo(tol) > 0) tol = relTol;
                ok = diff.CompareTo(tol) <= 0;
            }
            o = AssertionOutcome.Check(spec.Name, ok, expectedText, ctx.Answer, $"expected {expectedText} but got {ctx.Answer} (difference {diff})");
        }
        return Task.FromResult<IReadOnlyList<AssertionOutcome>>([o]);
    }

    private static string Raw(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText();

    private static Rational? Tol(JsonElement cfg, string name)
    {
        if (!cfg.TryGetProperty(name, out var t) || t.ValueKind == JsonValueKind.Null) return null;
        return Rational.TryParse(Raw(t), out var r) ? r : throw new ArgumentException($"{name} '{Raw(t)}' is not a number");
    }
}
