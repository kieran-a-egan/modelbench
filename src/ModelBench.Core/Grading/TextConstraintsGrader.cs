using ModelBench.Core.Domain;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModelBench.Core.Grading;

/// <summary>
/// Config: { constraints: [{ kind, ... }] }. One assertion per constraint, named "&lt;kind&gt;[i]".
/// Counting rules:
/// words = maximal runs of non-whitespace characters;
/// lines = lines (split on '\n') containing at least one non-whitespace character;
/// bullets = lines that, after trimming leading whitespace, start with "- " or "* ".
/// Kinds: word-count {min?,max?}, max-words {max}, contains/not-contains {text,caseSensitive?=true}, regex {pattern} (1 s timeout),
/// line-count {min?,max?}, starts-with {text}, ends-with {text} (ordinal, on the answer), all-lowercase, no-letter {letter} (case-insensitive),
/// json-keys {keys,exact?} (answer must be a JSON object), bullet-count {count}.
/// </summary>
public sealed class TextConstraintsGrader : IGrader
{
    public string Type => "text-constraints";

    public Task<IReadOnlyList<AssertionOutcome>> GradeAsync(GraderSpec spec, GradingContext ctx)
        => Task.FromResult<IReadOnlyList<AssertionOutcome>>(
            spec.Config.GetProperty("constraints").EnumerateArray().Select((c, i) => Evaluate(c, i, ctx.Answer)).ToList());

    private static AssertionOutcome Evaluate(JsonElement c, int i, string? text)
    {
        var kind = c.GetProperty("kind").GetString()!;
        var (name, expected) = ($"{kind}[{i}]", c.GetRawText());
        if (text is null) return AssertionOutcome.Check(name, false, expected, null, "no answer");
        var (ok, actual) = Test(kind, c, text);
        return AssertionOutcome.Check(name, ok, expected, actual, $"{kind} violated (actual: {actual})");
    }

    private static int? Int(JsonElement c, string p) => c.TryGetProperty(p, out var v) ? v.GetInt32() : null;
    private static string Str(JsonElement c, string p) => c.GetProperty(p).GetString()!;
    private static bool InRange(int n, JsonElement c) => n >= (Int(c, "min") ?? int.MinValue) && n <= (Int(c, "max") ?? int.MaxValue);
    private static string Head(string s) => (s.Length > 40 ? s[..40] + "…" : s).Replace("\n", "\\n");

    private static (bool, string) Test(string kind, JsonElement c, string text)
    {
        var lines = text.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        switch (kind)
        {
            case "word-count": { var n = Regex.Matches(text, @"\S+").Count; return (InRange(n, c), n.ToString()); }
            case "max-words": { var n = Regex.Matches(text, @"\S+").Count; return (n <= Int(c, "max"), n.ToString()); }
            case "contains" or "not-contains":
                {
                    var cmp = c.TryGetProperty("caseSensitive", out var cs) && !cs.GetBoolean() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    var found = text.Contains(Str(c, "text"), cmp);
                    return (found == (kind == "contains"), found ? "present" : "absent");
                }
            case "regex":
                try { var m = Regex.IsMatch(text, Str(c, "pattern"), RegexOptions.None, TimeSpan.FromSeconds(1)); return (m, m ? "match" : "no match"); }
                catch (RegexMatchTimeoutException) { return (false, "regex timed out after 1s"); }
                catch (ArgumentException ex) { return (false, $"invalid pattern: {ex.Message}"); }
            case "line-count": return (InRange(lines.Count, c), lines.Count.ToString());
            case "starts-with": return (text.StartsWith(Str(c, "text"), StringComparison.Ordinal), Head(text));
            case "ends-with": return (text.EndsWith(Str(c, "text"), StringComparison.Ordinal), Head(text.Length > 40 ? text[^40..] : text));
            case "all-lowercase": return (text == text.ToLowerInvariant(), Head(text));
            case "no-letter": { var has = text.Contains(Str(c, "letter"), StringComparison.OrdinalIgnoreCase); return (!has, has ? "present" : "absent"); }
            case "bullet-count":
                {
                    var n = lines.Count(l => l.TrimStart().StartsWith("- ") || l.TrimStart().StartsWith("* "));
                    return (n == Int(c, "count"), n.ToString());
                }
            case "json-keys":
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) return (false, "not a JSON object");
                    var have = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
                    var want = c.GetProperty("keys").EnumerateArray().Select(k => k.GetString()!).ToList();
                    var exact = c.TryGetProperty("exact", out var e) && e.GetBoolean();
                    return (want.All(have.Contains) && (!exact || have.All(want.Contains)), string.Join(",", have));
                }
                catch (JsonException) { return (false, "not valid JSON"); }
            default: throw new ArgumentException($"unknown constraint kind '{kind}'");
        }
    }
}
