using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelBench.Core.Domain;
using ModelBench.Core.Generation;

namespace ModelBench.Core.Families;

/// <summary>
/// A seeded access-control policy (3..15 rules, first-match or deny-overrides, default deny) and 3..20 cases.
/// Every rule is decisive for at least one case. Validate re-parses the prompt text and evaluates with a different algorithm.
/// </summary>
public sealed class PolicyEvaluationFamily : IBenchmarkFamily
{
    public string Id => "policy-evaluation";
    public int Version => 1;
    public string Category => "policy-evaluation";
    public string Description => "Apply a prose access-control policy with stated precedence to a list of cases.";
    public ExecutionMode Mode => ExecutionMode.SingleTurn;

    private static readonly string[] Attrs = ["role", "resource", "action", "location", "clearance"];
    private static readonly string[][] Values =
    [
        ["admin", "manager", "engineer", "contractor", "guest"],
        ["finance", "hr", "code", "public", "logs"],
        ["read", "write", "delete"],
        ["office", "remote"],
        ["1", "2", "3", "4", "5"],
    ];
    private const string FirstMatch = "first-match", DenyOverrides = "deny-overrides";
    private const string FirstMatchText = "Rules are checked in numeric order; the first rule whose conditions all hold decides the case.";
    private const string DenyOverridesText = "A case is denied if any matching rule says deny; otherwise it is allowed if any matching rule says allow.";
    private static readonly int[] RuleCounts = [3, 6, 9, 12, 15];
    private static readonly int[] CaseCounts = [3, 6, 10, 15, 20];

    private sealed record Cond(int Attr, string Op, string Value);
    private sealed record Rule(Cond[] Conds, string Effect);
    private sealed record Ref(string Mode, Rule[] Rules, string[][] Cases);

    // ---------------------------------------------------------------- generation

    public GeneratedTask Generate(Rng rng, int c)
    {
        if (c is < Complexity.Min or > Complexity.Max) throw new ArgumentOutOfRangeException(nameof(c));
        while (true)
        {
            var mode = c < 3 || rng.Chance(0.5) ? FirstMatch : DenyOverrides;
            var rules = Enumerable.Range(0, RuleCounts[c - 1]).Select(_ => NewRule(rng, c, mode)).ToArray();
            var cases = new List<string[]>();
            for (var i = 0; i < rules.Length && cases.Count == i; i++)
                for (var attempt = 0; attempt < 300; attempt++)
                {
                    var cand = Satisfying(rng, rules[i]);
                    if (Decisive(rules, mode, cand) != i) continue;
                    cases.Add(cand);
                    break;
                }
            if (cases.Count < rules.Length) continue; // some rule unreachable: resample from the same rng
            while (cases.Count < CaseCounts[c - 1]) cases.Add(Attrs.Select((_, a) => rng.Pick(Values[a])).ToArray());
            rng.Shuffle(cases);
            return Build(new Ref(mode, rules, cases.ToArray()));
        }
    }

    private static Rule NewRule(Rng rng, int c, string mode)
    {
        var order = Enumerable.Range(0, Attrs.Length).ToList();
        rng.Shuffle(order);
        var conds = order.Take(rng.Range(1, c < 2 ? 1 : c < 3 ? 2 : 3)).Select(a =>
            a == 4 ? new Cond(a, rng.Chance(0.5) ? "at least" : "below", rng.Range(2, 4).ToString())
                   : new Cond(a, rng.Chance(0.25) ? "is not" : "is", rng.Pick(Values[a]))).ToArray();
        return new Rule(conds, rng.Chance(mode == FirstMatch ? 0.5 : 0.35) ? "allow" : "deny");
    }

    private static string[] Satisfying(Rng rng, Rule r)
    {
        var v = Attrs.Select((_, a) => rng.Pick(Values[a])).ToArray();
        foreach (var k in r.Conds)
            v[k.Attr] = k.Op switch
            {
                "is" => k.Value,
                "is not" => rng.Pick(Values[k.Attr].Where(x => x != k.Value).ToList()),
                "at least" => rng.Range(int.Parse(k.Value), 5).ToString(),
                _ => rng.Range(1, int.Parse(k.Value) - 1).ToString(),
            };
        return v;
    }

    private static bool Holds(Cond k, string[] v) => k.Op switch
    {
        "is" => v[k.Attr] == k.Value,
        "is not" => v[k.Attr] != k.Value,
        "at least" => int.Parse(v[k.Attr]) >= int.Parse(k.Value),
        _ => int.Parse(v[k.Attr]) < int.Parse(k.Value),
    };

    /// <summary>Generator evaluator: index of the rule that decides the case (-1 = default deny).</summary>
    private static int Decisive(Rule[] rules, string mode, string[] v)
    {
        var firstAllow = -1;
        for (var i = 0; i < rules.Length; i++)
        {
            if (!rules[i].Conds.All(k => Holds(k, v))) continue;
            if (mode == FirstMatch) return i;
            if (rules[i].Effect == "deny") return i;
            if (firstAllow < 0) firstAllow = i;
        }
        return firstAllow;
    }

    private static string Decide(Rule[] rules, string mode, string[] v)
    {
        var d = Decisive(rules, mode, v);
        return d < 0 ? "deny" : rules[d].Effect;
    }

    private static string Phrase(Cond k) => $"{Attrs[k.Attr]} {(k.Op is "is" or "is not" ? k.Op : "is " + k.Op)} {k.Value}";

    private static GeneratedTask Build(Ref r)
    {
        var sb = new StringBuilder("Below is an access-control policy for a document system, followed by cases. Decide each case under the policy.\n\nRules:\n");
        for (var i = 0; i < r.Rules.Length; i++)
            sb.Ln($"Rule {i + 1}: {r.Rules[i].Effect} when {string.Join(" and ", r.Rules[i].Conds.Select(Phrase))}.");
        sb.Ln().Ln($"Precedence: {(r.Mode == FirstMatch ? FirstMatchText : DenyOverridesText)} If no rule matches, the decision is deny.")
            .Ln().Ln("Cases:");
        var ids = Enumerable.Range(1, r.Cases.Length).Select(i => $"case-{i}").ToArray();
        for (var i = 0; i < ids.Length; i++)
            sb.Ln($"{ids[i]}: {string.Join(", ", Attrs.Select((a, k) => $"{a}={r.Cases[i][k]}"))}");
        sb.Ln().Append("Reply with one JSON object mapping every case id to \"allow\" or \"deny\", for example {\"case-1\":\"allow\",\"case-2\":\"deny\"}, inside <answer></answer> tags.");

        var decisions = r.Cases.Select(v => Decide(r.Rules, r.Mode, v)).ToArray();
        var schema = new
        {
            type = "object",
            required = ids,
            additionalProperties = false,
            properties = ids.ToDictionary(i => i, _ => new { type = "string", @enum = new[] { "allow", "deny" } }),
        };
        var graders = new List<GraderSpec> { new("json-schema", "shape", JsonUtil.Element(new { schema })) };
        graders.AddRange(ids.Select((id, i) => new GraderSpec("structural", id, JsonUtil.Element(new { expected = decisions[i], key = id }))));

        string Json(Func<int, string> f) => "{" + string.Join(",", ids.Select((id, i) => $"\"{id}\":\"{f(i)}\"")) + "}";
        string Flip(string d) => d == "allow" ? "deny" : "allow";
        var good = Json(i => decisions[i]);
        var bad = new List<IReadOnlyList<ChatMessage>>
        {
            Turn($"<answer>{Json(i => Flip(decisions[i]))}</answer>"),
            Turn($"<answer>{Json(i => i == 0 ? Flip(decisions[i]) : decisions[i])}</answer>"),
            Turn(good),
        };
        return new GeneratedTask(TaskPrompt.Simple(sb.ToString().Replace("\r\n", "\n"), AnswerFormat.Json), new TaskHidden(graders, null, JsonUtil.Element(r)),
            Turn($"<answer>{good}</answer>"), bad);
    }

    private static IReadOnlyList<ChatMessage> Turn(string s) => [new ChatMessage(Role.Assistant, s)];

    // ---------------------------------------------------------------- independent validation

    public ValidationResult Validate(GeneratedTask task)
    {
        var problems = new List<string>();
        try
        {
            var p = task.Prompt.User;
            if (task.Prompt.AnswerFormat != AnswerFormat.Json) problems.Add("answer format must be Json");
            if (!p.Contains("<answer></answer>")) problems.Add("prompt does not state the answer format");

            // Re-parse the policy from the prose and evaluate with a match-set formulation.
            var ruleLines = Regex.Matches(p, @"^Rule (\d+): (allow|deny) when (.+)\.$", RegexOptions.Multiline);
            var rules = ruleLines.Select(m => (Effect: m.Groups[2].Value,
                Conds: m.Groups[3].Value.Split(" and ").Select(t => Regex.Match(t, @"^(\w+) is (not |at least |below )?(\w+)$")).ToList())).ToList();
            if (rules.Count is < 3 or > 15) problems.Add($"rule count {rules.Count} outside 3..15");
            var firstMatch = p.Contains(FirstMatchText);
            if (firstMatch == p.Contains(DenyOverridesText)) problems.Add("prompt must state exactly one precedence");
            if (!p.Contains("If no rule matches, the decision is deny.")) problems.Add("prompt does not state the default");

            var cases = Regex.Matches(p, @"^(case-\d+): (.+)$", RegexOptions.Multiline)
                .Select(m => (Id: m.Groups[1].Value, Attr: m.Groups[2].Value.Split(", ").Select(kv => kv.Split('=')).ToList())).ToList();
            if (cases.Count is < 3 or > 20) problems.Add($"case count {cases.Count} outside 3..20");
            if (cases.Select(c => c.Id).Distinct().Count() != cases.Count) problems.Add("duplicate case ids");
            if (cases.Any(c => c.Attr.Count != Attrs.Length || c.Attr.Any(kv => kv.Length != 2))) problems.Add("a case does not specify every attribute exactly once");

            var exercised = new bool[rules.Count];
            foreach (var c in cases)
            {
                var attr = c.Attr.ToDictionary(kv => kv[0], kv => kv[1]);
                var matching = Enumerable.Range(0, rules.Count).Where(i => rules[i].Conds.All(m =>
                {
                    var (a, op, val) = (attr[m.Groups[1].Value], m.Groups[2].Value.Trim(), m.Groups[3].Value);
                    return op switch
                    {
                        "" => a == val,
                        "not" => a != val,
                        "at least" => int.Parse(a) >= int.Parse(val),
                        _ => int.Parse(a) < int.Parse(val),
                    };
                })).ToList();
                var anyDeny = matching.Any(i => rules[i].Effect == "deny");
                var decision = matching.Count == 0 ? "deny" : firstMatch ? rules[matching.Min()].Effect : anyDeny ? "deny" : "allow";
                foreach (var i in matching)
                    if (rules[i].Effect == decision && (firstMatch ? i == matching.Min() : decision == "deny" || !anyDeny)) exercised[i] = true;
                var g = task.Hidden.Graders.SingleOrDefault(x => x.Type == "structural" && x.Name == c.Id);
                if (g is null) problems.Add($"no grader for {c.Id}");
                else if (g.Config.GetProperty("expected").GetString() != decision) problems.Add($"{c.Id}: expected {g.Config.GetProperty("expected").GetString()} != independent {decision}");
            }
            if (exercised.Any(x => !x)) problems.Add("a rule is never decisive for any case");
            if (task.Hidden.Graders.Count(x => x.Type == "structural") != cases.Count) problems.Add("grader count differs from case count");
            if (task.KnownBadTurns.Count < 2) problems.Add("need at least two known-bad turns");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            problems.Add($"malformed task: {ex.Message}");
        }
        return problems.Count == 0 ? ValidationResult.Valid : new ValidationResult(false, problems);
    }
}
