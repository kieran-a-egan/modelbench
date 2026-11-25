using System.Text.RegularExpressions;
using ModelBench.Core.Domain;

namespace ModelBench.Core.Grading;

/// <summary>Config: { expected: string, normalize?: "none"|"trim"|"whitespace"|"casefold" }.</summary>
public sealed partial class ExactGrader : IGrader
{
    public string Type => "exact";

    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();

    public Task<IReadOnlyList<AssertionOutcome>> GradeAsync(GraderSpec spec, GradingContext ctx)
    {
        var expected = spec.Config.GetProperty("expected").GetString() ?? "";
        var mode = spec.Config.TryGetProperty("normalize", out var n) ? n.GetString() : "none";
        AssertionOutcome o;
        if (ctx.Answer is null) o = AssertionOutcome.Check(spec.Name, false, expected, null, "no answer");
        else
        {
            var ok = Normalize(expected, mode) == Normalize(ctx.Answer, mode);
            o = AssertionOutcome.Check(spec.Name, ok, expected, ctx.Answer, $"expected \"{expected}\" but got \"{ctx.Answer}\" (normalize={mode})");
        }
        return Task.FromResult<IReadOnlyList<AssertionOutcome>>([o]);
    }

    private static string Normalize(string s, string? mode) => mode switch
    {
        null or "none" => s,
        "trim" => s.Trim(),
        "whitespace" => Spaces().Replace(s, " ").Trim(),
        "casefold" => s.Trim().ToLowerInvariant(),
        _ => throw new ArgumentException($"unknown normalize mode '{mode}'"),
    };
}
