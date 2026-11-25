using ModelBench.Core.Domain;
using ModelBench.Core.Sandbox;

namespace ModelBench.Core.Grading;

/// <summary>Config: { language: "python"|"csharp", entryPoint }. One assertion "compiles"; its actual is the compile errors.</summary>
public sealed class CompileGrader : IGrader
{
    public string Type => "compile";

    public async Task<IReadOnlyList<AssertionOutcome>> GradeAsync(GraderSpec spec, GradingContext ctx)
    {
        if (ctx.Answer is null) return [AssertionOutcome.Check("compiles", false, "compiles", null, "no answer")];
        var req = new SandboxRequest(CodeSupport.Language(spec.Config), SandboxAction.Compile, ctx.Answer,
            spec.Config.GetProperty("entryPoint").GetString()!, [], CodeSupport.CompileTimeoutMs);
        var r = await ctx.Sandbox.ExecuteAsync(req, ctx.CancellationToken);
        var errors = r.TimedOut ? "timed out" : r.CompileErrors ?? (r.Compiled ? null : "crashed: " + r.StdErrTail);
        return [AssertionOutcome.Check("compiles", errors is null, "compiles", errors ?? "", errors)];
    }
}
