using System.Text.Json;
using System.Text.RegularExpressions;
using ModelBench.Core.Domain;

namespace ModelBench.Core.Grading;

/// <summary>Deterministic answer location (ADR 0005): strip reasoning blocks, then take the last match.</summary>
public static partial class AnswerExtractor
{
    private const RegexOptions Opts = RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"<(think|thinking)>.*?</\1>", Opts)] private static partial Regex ClosedThink();
    [GeneratedRegex(@"^.*</(think|thinking)>", Opts)] private static partial Regex OrphanClose();
    [GeneratedRegex(@"<(think|thinking)>.*$", Opts)] private static partial Regex UnclosedOpen();
    [GeneratedRegex(@"<answer>(.*?)</answer>", Opts)] private static partial Regex Tagged();
    [GeneratedRegex(@"```[^\r\n]*\r?\n(.*?)```", Opts)] private static partial Regex Fenced();
    [GeneratedRegex(@"^```[^\r\n]*\r?\n(.*?)\s*```$", Opts)] private static partial Regex WholeFence();

    /// <summary>Removes closed think blocks, text before a stray closing tag (template-opened thinking) and an unterminated block.</summary>
    public static string StripReasoning(string text)
    {
        text = ClosedThink().Replace(text, "");
        text = OrphanClose().Replace(text, "");
        text = UnclosedOpen().Replace(text, "");
        return text.Trim();
    }

    /// <summary>The extracted answer, or null when the declared format is not found. Input must already be reasoning-stripped.</summary>
    public static string? Extract(string? text, AnswerFormat format)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return format switch
        {
            AnswerFormat.Tagged => Last(Tagged(), text),
            AnswerFormat.CodeBlock => Last(Fenced(), text),
            AnswerFormat.Json => ParsesAsJson(Unfence(Last(Tagged(), text)) ?? Last(Fenced(), text)), // the prompt allows fences inside the tags
            _ => text.Trim(),
        };
    }

    private static string? Last(Regex re, string text)
    {
        Match? last = null;
        foreach (Match m in re.Matches(text)) last = m;
        var v = last?.Groups[1].Value.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static string? Unfence(string? body) => body is not null && WholeFence().Match(body) is { Success: true } m ? m.Groups[1].Value.Trim() : body;

    private static string? ParsesAsJson(string? candidate)
    {
        if (candidate is null) return null;
        try { using var _ = JsonDocument.Parse(candidate); return candidate; }
        catch (JsonException) { return null; }
    }
}
