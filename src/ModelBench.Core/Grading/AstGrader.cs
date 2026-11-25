using ModelBench.Core.Domain;
using ModelBench.Core.Sandbox;

namespace ModelBench.Core.Grading;

/// <summary>
/// Config: { language, rules: [{ kind: "defines"|"forbids-import"|"forbids-call"|"requires-loop"|"forbids-recursion", name? }] }.
/// One sandbox Inspect; every rule is evaluated on the host from the returned AstFacts. One assertion per rule.
/// </summary>
public sealed class AstGrader : IGrader
{
    public string Type => "ast";

    private sealed record Rule(string Kind, string? Name)
    {
        public string Label => Name is null ? Kind : $"{Kind}:{Name}";
        public string Need => Name ?? throw new ArgumentException($"ast rule '{Kind}' needs a name");
    }

    public async Task<IReadOnlyList<AssertionOutcome>> GradeAsync(GraderSpec spec, GradingContext ctx)
    {
        var rules = spec.Config.GetProperty("rules").EnumerateArray()
            .Select(r => new Rule(r.GetProperty("kind").GetString()!, r.TryGetProperty("name", out var n) ? n.GetString() : null)).ToList();
        IReadOnlyList<AssertionOutcome> FailAll(string why) => rules.Select(r => AssertionOutcome.Check(r.Label, false, r.Label, null, why)).ToList();

        if (ctx.Answer is null) return FailAll("no answer");
        var req = new SandboxRequest(CodeSupport.Language(spec.Config), SandboxAction.Inspect, ctx.Answer, "", [], CodeSupport.CompileTimeoutMs);
        var r = await ctx.Sandbox.ExecuteAsync(req, ctx.CancellationToken);
        if (r.TimedOut) return FailAll("timed out");
        if (!r.Compiled || r.AstFacts is not { } facts) return FailAll("could not be parsed: " + (r.CompileErrors ?? r.StdErrTail));

        List<string> List(string key) => facts.GetProperty(key).EnumerateArray().Select(e => e.GetString()!).ToList();
        return rules.Select(rule =>
        {
            var (ok, why) = rule.Kind switch
            {
                "defines" => (List("functions").Contains(rule.Need) || List("classes").Contains(rule.Need), $"'{rule.Name}' is not defined"),
                "forbids-import" => (!List("imports").Any(i => i == rule.Need || i.StartsWith(rule.Need + ".", StringComparison.Ordinal)), $"imports '{rule.Name}'"),
                "forbids-call" => (!List("calledNames").Contains(rule.Need), $"calls '{rule.Name}'"),
                "requires-loop" => (facts.GetProperty("hasLoop").GetBoolean(), "contains no loop"),
                "forbids-recursion" => (List("recursiveFunctions").Count == 0, "is recursive: " + string.Join(", ", List("recursiveFunctions"))),
                _ => throw new ArgumentException($"unknown ast rule '{rule.Kind}'"),
            };
            return AssertionOutcome.Check(rule.Label, ok, rule.Label, ok ? "ok" : why, "code " + why);
        }).ToList();
    }
}
