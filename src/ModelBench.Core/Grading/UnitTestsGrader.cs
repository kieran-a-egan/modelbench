using ModelBench.Core.Domain;
using ModelBench.Core.Sandbox;

namespace ModelBench.Core.Grading;

/// <summary>
/// Config: { language, entryPoint, cases: [{ args: [...], expected: json }], tolerance?: number }.
/// One sandbox Run with every case's args (never the expected values); outputs are compared on the host.
/// One assertion per case, named "case[i]".
/// </summary>
public class UnitTestsGrader : IGrader
{
    public virtual string Type => "unit-tests";
    protected virtual string Prefix => "case";

    public async Task<IReadOnlyList<AssertionOutcome>> GradeAsync(GraderSpec spec, GradingContext ctx)
    {
        var cases = spec.Config.GetProperty("cases").EnumerateArray().ToList();
        var expected = cases.Select(c => c.GetProperty("expected")).ToList();
        IReadOnlyList<AssertionOutcome> FailAll(string why) =>
            expected.Select((e, i) => AssertionOutcome.Check($"{Prefix}[{i}]", false, e.GetRawText(), null, why)).ToList();

        if (ctx.Answer is null) return FailAll("no answer");
        var tol = spec.Config.TryGetProperty("tolerance", out var t) ? t.GetDouble() : 0;
        var req = new SandboxRequest(CodeSupport.Language(spec.Config), SandboxAction.Run, ctx.Answer,
            spec.Config.GetProperty("entryPoint").GetString()!, cases.Select(c => c.GetProperty("args")).ToList(), CodeSupport.CaseTimeoutMs);
        var r = await ctx.Sandbox.ExecuteAsync(req, ctx.CancellationToken);
        if (CodeSupport.Unusable(r, cases.Count) is { } why) return FailAll(why);

        return expected.Select((e, i) =>
        {
            var c = r.Cases[i];
            var ok = c.Error is null && CodeSupport.JsonEqual(c.Output ?? CodeSupport.Null, e, tol);
            return AssertionOutcome.Check($"{Prefix}[{i}]", ok, e.GetRawText(), CodeSupport.Actual(c),
                c.Error is null ? $"expected {e.GetRawText()} but got {CodeSupport.Actual(c)}" : "error: " + c.Error);
        }).ToList();
    }
}
