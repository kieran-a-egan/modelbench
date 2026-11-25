using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelBench.Core.Domain;
using ModelBench.Core.Generation;

namespace ModelBench.Core.Families;

/// <summary>
/// A short writing task with N = 2 * complexity verifiable constraints (text-constraints grader).
/// A reference text is built first from word lists; each constraint is then derived from facts true of that text,
/// so the set is satisfiable by construction. The vocabulary never contains the letters j, q, x, z.
/// </summary>
public sealed class InstructionFidelityFamily : IBenchmarkFamily
{
    public string Id => "instruction-fidelity";
    public int Version => 1;
    public string Category => "instruction-fidelity";
    public string Description => "Write a short note on a topic while satisfying several mechanically checkable constraints.";
    public ExecutionMode Mode => ExecutionMode.SingleTurn;

    private static readonly string[] Topics = ["harbor lights", "mountain railways", "community gardens", "night markets", "lighthouse keepers", "river ferries", "street musicians", "desert caravans"];
    private static readonly string[] Nouns = ["people", "stories", "machines", "gardens", "visitors", "tools", "seasons", "letters", "bridges", "markets", "engines", "workers"];
    private static readonly string[] Verbs = ["gather", "build", "remember", "change", "carry", "protect", "share", "repair", "follow", "welcome"];
    private static readonly string[] Adjs = ["calm", "bright", "old", "careful", "busy", "gentle", "steady", "local", "patient", "simple"];
    private static readonly string[] Openers = ["to begin,", "first of all,", "in short,", "for the record,"];
    private static readonly string[] Closers = ["that is all.", "thank you for reading.", "and so it goes.", "the end."];
    private static readonly string[] Decoys = ["zebra", "violin", "quartz", "lantern", "pyramid", "jacket"];
    private static readonly string[] Regexes = [@"\b[a-z]{4}\b", @"\b[a-z]{8,}\b", @"\b(of|in|on|the)\b"];
    private static readonly string[] Kinds = ["word-count", "max-words", "contains", "not-contains", "regex", "line-count", "starts-with", "ends-with", "all-lowercase", "no-letter", "bullet-count"];

    private sealed record Ref(string Topic, string Text);

    private static string Sentence(Rng rng, int extra)
    {
        var sb = new StringBuilder($"the {rng.Pick(Adjs)} {rng.Pick(Nouns)} {rng.Pick(Verbs)} the {rng.Pick(Adjs)} {rng.Pick(Nouns)}");
        for (var i = 0; i < extra; i++) sb.Append($" and the {rng.Pick(Adjs)} {rng.Pick(Nouns)}");
        return sb.ToString();
    }

    public GeneratedTask Generate(Rng rng, int c)
    {
        if (c is < Complexity.Min or > Complexity.Max) throw new ArgumentOutOfRangeException(nameof(c));
        var topic = rng.Pick(Topics);
        var (opener, closer, bullets) = (rng.Pick(Openers), rng.Pick(Closers), rng.Range(1, 4));
        var lines = new List<string> { $"{opener} the {topic} are worth a closer look {Sentence(rng, rng.Range(0, c))}" };
        for (var i = 0; i < bullets; i++) lines.Add($"- {Sentence(rng, rng.Range(0, c))}");
        lines.Add($"{Sentence(rng, rng.Range(0, c))} {closer}");
        var text = string.Join("\n", lines);
        var words = Regex.Matches(text, @"\S+").Select(m => m.Value).ToList();
        var w = words.Count;

        var constraints = new List<JsonElement>();
        var order = Kinds.ToList();
        rng.Shuffle(order);
        foreach (var kind in order)
        {
            if (constraints.Count == 2 * c) break;
            object? spec = kind switch
            {
                "word-count" => new { kind, min = w - rng.Range(5, 20), max = w + rng.Range(5, 20) },
                "max-words" => new { kind, max = w + rng.Range(5, 20) },
                "contains" => new { kind, text = rng.Pick(words.Where(x => x.Length >= 6 && char.IsLetter(x[^1])).DefaultIfEmpty("people").ToList()) },
                "not-contains" => new { kind, text = Decoys.First(d => !text.Contains(d)) },
                "regex" => Regexes.Where(p => Regex.IsMatch(text, p)).ToList() is { Count: > 0 } ok ? new { kind, pattern = rng.Pick(ok) } : null,
                "line-count" => new { kind, min = lines.Count, max = lines.Count + 2 },
                "starts-with" => new { kind, text = opener },
                "ends-with" => new { kind, text = closer },
                "all-lowercase" => new { kind },
                "no-letter" => "zqxjkvw".Where(l => !text.Contains(l)).ToList() is { Count: > 0 } free ? new { kind, letter = rng.Pick(free).ToString() } : null,
                _ => new { kind, count = bullets },
            };
            if (spec is not null) constraints.Add(JsonUtil.Element(spec));
        }

        var user = new StringBuilder()
            .AppendLine($"Write a short note about {topic}. It must satisfy every one of the following constraints:")
            .AppendLine();
        for (var i = 0; i < constraints.Count; i++) user.AppendLine($"{i + 1}. {Describe(constraints[i])}");
        user.AppendLine()
            .Append("Definitions: a word is a maximal run of non-whitespace characters; a line is a non-empty line; ")
            .AppendLine("a bullet line starts with \"- \" or \"* \" (after any leading whitespace).")
            .Append("Put only the finished note inside <answer></answer> tags (it may span several lines). Nothing else may appear inside the tags.");

        var hidden = new TaskHidden(
            [new GraderSpec("text-constraints", "constraints", JsonUtil.Element(new { constraints }))],
            null, JsonUtil.Element(new Ref(topic, text)));
        var bad = new List<IReadOnlyList<ChatMessage>> { Turn(text) }; // right text, no <answer> tags
        foreach (var i in new[] { 0, constraints.Count - 1 }.Distinct())
            bad.Add(Turn($"<answer>{Violate(constraints[i], text)}</answer>"));
        if (bad.Count < 3) bad.Add(Turn("<answer></answer>"));
        return new GeneratedTask(TaskPrompt.Simple(user.ToString().Replace("\r\n", "\n")), hidden, Turn($"<answer>{text}</answer>"), bad);
    }

    private static IReadOnlyList<ChatMessage> Turn(string s) => [new ChatMessage(Role.Assistant, s)];

    private static string Describe(JsonElement c)
    {
        string S(string p) => c.GetProperty(p).GetString()!;
        int? I(string p) => c.TryGetProperty(p, out var v) ? v.GetInt32() : null;
        return c.GetProperty("kind").GetString() switch
        {
            "word-count" => $"Use between {I("min")} and {I("max")} words (inclusive).",
            "max-words" => $"Use at most {I("max")} words.",
            "contains" => $"Include the text \"{S("text")}\" (case-sensitive).",
            "not-contains" => $"Do not include the text \"{S("text")}\" anywhere.",
            "regex" => $"The note must match the regular expression {S("pattern")} somewhere.",
            "line-count" => $"Use between {I("min")} and {I("max")} non-empty lines (inclusive).",
            "starts-with" => $"Begin the note exactly with \"{S("text")}\".",
            "ends-with" => $"End the note exactly with \"{S("text")}\".",
            "all-lowercase" => "Use only lowercase letters.",
            "no-letter" => $"Never use the letter \"{S("letter")}\" in either case.",
            "bullet-count" => $"Include exactly {I("count")} bullet lines.",
            var k => throw new InvalidOperationException($"unknown kind {k}"),
        };
    }

    /// <summary>A text that breaks exactly the given constraint (used for known-bad turns).</summary>
    private static string Violate(JsonElement c, string text)
    {
        string S(string p) => c.GetProperty(p).GetString()!;
        return c.GetProperty("kind").GetString() switch
        {
            "word-count" or "max-words" => text + string.Concat(Enumerable.Repeat(" more", 400)),
            "contains" => text.Replace(S("text"), "", StringComparison.OrdinalIgnoreCase),
            "not-contains" => text + " " + S("text"),
            "regex" => "!!!",
            "line-count" => text.Replace("\n", " "),
            "starts-with" => "# " + text,
            "ends-with" => text + " !!",
            "all-lowercase" => text.ToUpperInvariant(),
            "no-letter" => text + " " + S("letter"),
            _ => text.Replace("\n- ", "\n"),
        };
    }

    // ---------------------------------------------------------------- independent validation

    public ValidationResult Validate(GeneratedTask task)
    {
        var problems = new List<string>();
        try
        {
            if (task.Prompt.AnswerFormat != AnswerFormat.Tagged) problems.Add("answer format must be Tagged");
            if (!task.Prompt.User.Contains("<answer></answer>")) problems.Add("prompt does not state the answer format");
            var cs = task.Hidden.Graders.Single(g => g.Type == "text-constraints").Config.GetProperty("constraints").EnumerateArray().ToList();
            if (cs.Count < 2 || cs.Count > 10) problems.Add($"constraint count {cs.Count} outside 2..10");
            var numbered = Regex.Matches(task.Prompt.User, @"^\d+\. ", RegexOptions.Multiline).Count;
            if (numbered != cs.Count) problems.Add($"prompt lists {numbered} constraints, grader has {cs.Count}");

            string? Answer(IReadOnlyList<ChatMessage> t) => Between(t.Last().Content);
            var reference = Answer(task.ReferenceTurns);
            if (reference is null) return ValidationResult.Fail("reference turn has no <answer>");
            foreach (var c in cs)
                if (!Holds(c, reference)) problems.Add($"reference text violates {c.GetRawText()}");
            if (task.KnownBadTurns.Count < 2) problems.Add("need at least two known-bad turns");
            foreach (var bad in task.KnownBadTurns)
                if (Answer(bad) is { } a && cs.All(c => Holds(c, a))) problems.Add("a known-bad turn satisfies every constraint");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            problems.Add($"malformed task: {ex.Message}");
        }
        return problems.Count == 0 ? ValidationResult.Valid : new ValidationResult(false, problems);
    }

    private static string? Between(string? s)
    {
        if (s is null) return null;
        var a = s.LastIndexOf("<answer>", StringComparison.Ordinal);
        var b = s.LastIndexOf("</answer>", StringComparison.Ordinal);
        return a < 0 || b < a ? null : s[(a + 8)..b].Trim();
    }

    /// <summary>Minimal independent checker: plain string operations, no use of the grader.</summary>
    private static bool Holds(JsonElement c, string t)
    {
        int? I(string p) => c.TryGetProperty(p, out var v) ? v.GetInt32() : null;
        string S(string p) => c.GetProperty(p).GetString()!;
        var words = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var lines = t.Split('\n').Select(l => l.Trim()).Where(l => l != "").ToList();
        switch (c.GetProperty("kind").GetString())
        {
            case "word-count": return words >= (I("min") ?? 0) && words <= (I("max") ?? int.MaxValue);
            case "max-words": return words <= I("max");
            case "contains": return t.IndexOf(S("text"), StringComparison.Ordinal) >= 0;
            case "not-contains": return t.IndexOf(S("text"), StringComparison.Ordinal) < 0;
            case "regex": return new Regex(S("pattern"), RegexOptions.None, TimeSpan.FromSeconds(1)).IsMatch(t);
            case "line-count": return lines.Count >= (I("min") ?? 0) && lines.Count <= (I("max") ?? int.MaxValue);
            case "starts-with": return t.StartsWith(S("text"), StringComparison.Ordinal);
            case "ends-with": return t.EndsWith(S("text"), StringComparison.Ordinal);
            case "all-lowercase": return t.All(ch => !char.IsUpper(ch));
            case "no-letter": return t.ToLowerInvariant().IndexOf(char.ToLowerInvariant(S("letter")[0])) < 0;
            case "bullet-count": return lines.Count(l => l.StartsWith("- ") || l.StartsWith("* ")) == I("count");
            default: throw new InvalidOperationException("unknown constraint kind");
        }
    }
}
