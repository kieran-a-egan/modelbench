using System.Text;
using System.Text.Json;
using ModelBench.Core.Domain;
using ModelBench.Core.Generation;
using ModelBench.Core.Grading;
using static ModelBench.Core.Families.CodeKit;

namespace ModelBench.Core.Families;

/// <summary>
/// Seeded function-writing tasks graded by running the candidate's code in the sandbox.
/// Complexity scales the task pool, the number of random (differential) cases (5..29), and adds performance (c >= 3)
/// and AST (c >= 4) graders. Expected values come from a C# reference solver at generation time and live only in TaskHidden.
/// </summary>
public sealed class CodeGenerationFamily : IBenchmarkFamily
{
    public string Id => "code-generation";
    public int Version => 3; // v2: platform-independent LF line endings; v3: performance reported, not scored (MB-002)
    public string Category => "code-generation";
    public string Description => "Write a Python or C# function from a specification; graded by compiling, hidden unit tests, seeded differential tests, a performance budget and AST rules.";
    public ExecutionMode Mode => ExecutionMode.SingleTurn;

    private sealed record Ref(string Task, JsonElement[][] Examples);

    private static object Case(CodeTask t, object[] args) => new { args, expected = t.Solve(Elems(args)) };

    public GeneratedTask Generate(Rng rng, int c)
    {
        if (c is < Complexity.Min or > Complexity.Max) throw new ArgumentOutOfRangeException(nameof(c));
        // Easy tasks only at c1; every task at c2; c >= 3 uses only tasks that have a performance case.
        var pool = CodeTasks.All.Where(t => c == 2 || (c == 1) == (t.Perf is null)).ToList();
        var task = rng.Pick(pool);

        var used = task.Examples.Concat(task.Edge).Select(J).ToList();
        var random = new List<object[]>();
        for (var attempts = 0; random.Count < 5 + 6 * (c - 1); attempts++)
        {
            if (attempts > 5000) throw new InvalidOperationException($"{task.Id}: input domain too small for {c}");
            var args = task.Gen(rng, c);
            if (!used.Contains(J(args))) { used.Add(J(args)); random.Add(args); }
        }

        var graders = new List<GraderSpec>
        {
            new("compile", "compile", JsonUtil.Element(new { language = task.Lang, entryPoint = task.Entry })),
            new("unit-tests", "unit-tests", JsonUtil.Element(new { language = task.Lang, entryPoint = task.Entry, cases = task.Edge.Select(a => Case(task, a)) }), 3),
            new("differential", "differential", JsonUtil.Element(new { language = task.Lang, entryPoint = task.Entry, cases = random.Select(a => Case(task, a)) }), 3),
        };
        var constraints = new StringBuilder();
        if (c >= 3 && task.Perf is { } perf)
        {
            var big = perf.Gen(rng);
            graders.Add(new("performance", "performance", JsonUtil.Element(new
            {
                language = task.Lang, entryPoint = task.Entry, args = big, expected = task.Solve(Elems(big)), budgetMs = perf.BudgetMs,
            }), Weight: 0, Required: false)); // MB-002: wall time is not reproducible, so it is reported, never scored
            constraints.Append("\nYour solution is also timed on a large input against a wall-clock target of ").Append(perf.BudgetMs / 1000.0)
                .Append(" seconds including interpreter start-up (reported, not scored), so use an efficient algorithm.");
        }
        if (c >= 4 && task.Forbid is { } forbid)
        {
            graders.Add(new("ast", "ast", JsonUtil.Element(new
            {
                language = task.Lang,
                rules = new object[] { new { kind = "defines", name = task.Method }, new { kind = "forbids-import", name = forbid } },
            })));
            constraints.Append("\nDo not import the `").Append(forbid).Append("` module.");
        }

        var py = task.Lang == "python";
        var user = new StringBuilder()
            .Append("Implement the following in ").Ln(py ? "Python 3." : "C#.").Ln()
            .Ln(task.Desc).Ln()
            .Ln("Signature:").Append("    ").Ln(task.Sig).Ln()
            .Ln("Examples (arguments and result in JSON notation):");
        foreach (var ex in task.Examples) user.Append("    ").Append(Call(task.Method, ex)).Append(" -> ").Ln(J(task.Solve(Elems(ex))));
        user.Ln().Append(py
                ? "Use only the standard library and define the function at module level."
                : "Define `public static class Solution` containing the public static method; System, System.Linq, System.Text and System.Collections.Generic are already imported.")
            .Append(constraints)
            .Append("\nReply with exactly one fenced code block containing the complete solution.");

        var bad = new List<IReadOnlyList<ChatMessage>>
        {
            Answer(task.Lang, task.Wrong),
            Answer(task.Lang, Broken(task.Ref)),
            Turn("I would solve this with a single pass and a hash map, but I won't write the code."),
        };
        // The quadratic answer is correct, so it passes now that performance is reported, not scored (MB-002).
        // CodeFamilyTests checks it is still reported over budget, which keeps each budget calibrated.

        return new GeneratedTask(new TaskPrompt(null, user.ToString(), AnswerFormat.CodeBlock, []),
            new TaskHidden(graders, null, JsonUtil.Element(new { task = task.Id, examples = task.Examples })),
            Answer(task.Lang, task.Ref), bad);
    }

    public ValidationResult Validate(GeneratedTask task)
    {
        var problems = new List<string>();
        if (task.Hidden.Reference is not { } reference) return ValidationResult.Fail("missing reference metadata");
        var r = JsonUtil.Deserialize<Ref>(reference.GetRawText());
        var t = CodeTasks.All.FirstOrDefault(x => x.Id == r.Task);
        if (t is null) return ValidationResult.Fail($"unknown task '{r.Task}'");

        var hiddenArgs = new List<string>();
        foreach (var g in task.Hidden.Graders)
        {
            List<JsonElement> cases = g.Type switch
            {
                "unit-tests" or "differential" => g.Config.GetProperty("cases").EnumerateArray().ToList(),
                "performance" => [g.Config],
                _ => [],
            };
            if (g.Type == "differential" && cases.Count is < 5 or > 30) problems.Add($"{cases.Count} random cases");
            for (var i = 0; i < cases.Count; i++)
            {
                var args = cases[i].GetProperty("args");
                hiddenArgs.Add(args.GetRawText());
                var actual = Element(t.Check(args.EnumerateArray().ToArray()));
                if (!CodeSupport.JsonEqual(actual, cases[i].GetProperty("expected")))
                    problems.Add($"{g.Type}[{i}]: stored {cases[i].GetProperty("expected").GetRawText()}, independent solver {actual.GetRawText()}");
            }
        }
        if (!new[] { "compile", "unit-tests", "differential" }.All(ty => task.Hidden.Graders.Any(g => g.Type == ty && g.Required))) problems.Add("missing required grader");
        if (r.Examples.Length < 2) problems.Add("fewer than 2 examples");
        foreach (var ex in r.Examples)
        {
            if (hiddenArgs.Contains("[" + string.Join(",", ex.Select(e => e.GetRawText())) + "]")) problems.Add("an example is also a hidden case");
            if (!task.Prompt.User.Contains(Call(t.Method, ex.Select(e => (object)e).ToArray()) + " -> " + Element(t.Check(ex)).GetRawText()))
                problems.Add("an example is missing from the prompt or disagrees with the independent solver");
        }
        if (t.Forbid is { } f && (t.Ref.Contains("import " + f) || t.Ref.Contains("from " + f))) problems.Add("reference uses the forbidden module");
        return problems.Count == 0 ? ValidationResult.Valid : new ValidationResult(false, problems);
    }
}
