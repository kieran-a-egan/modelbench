using System.Text.Json;
using ModelBench.Core.Domain;

namespace ModelBench.Core.Grading;

/// <summary>Config: { expected: json, unorderedArrays?: bool }. One failed assertion per mismatching JSON path, or a single pass.</summary>
public sealed class StructuralGrader : IGrader
{
    public string Type => "structural";

    public Task<IReadOnlyList<AssertionOutcome>> GradeAsync(GraderSpec spec, GradingContext ctx)
        => Task.FromResult(Grade(spec, ctx));

    private static IReadOnlyList<AssertionOutcome> Grade(GraderSpec spec, GradingContext ctx)
    {
        var expected = spec.Config.GetProperty("expected");
        var unordered = spec.Config.TryGetProperty("unorderedArrays", out var u) && u.GetBoolean();
        var expectedText = expected.GetRawText();
        if (ctx.Answer is null) return [AssertionOutcome.Check(spec.Name, false, expectedText, null, "no answer")];

        JsonDocument doc;
        try { doc = JsonDocument.Parse(ctx.Answer); }
        catch (JsonException ex) { return [AssertionOutcome.Check(spec.Name, false, expectedText, ctx.Answer, $"answer is not valid JSON: {ex.Message}")]; }
        using (doc)
        {
            var diffs = new List<AssertionOutcome>();
            Compare("$", expected, doc.RootElement, unordered, (path, e, a, why) =>
                diffs.Add(AssertionOutcome.Check($"{spec.Name}:{path}", false, e, a, why)));
            return diffs.Count == 0 ? [AssertionOutcome.Check(spec.Name, true, expectedText, ctx.Answer)] : diffs;
        }
    }

    private static bool SameKind(JsonElement e, JsonElement a)
        => e.ValueKind == a.ValueKind || (IsBool(e) && IsBool(a));

    private static bool IsBool(JsonElement e) => e.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static void Compare(string path, JsonElement e, JsonElement a, bool unordered, Action<string, string, string, string> report)
    {
        if (!SameKind(e, a)) { report(path, e.GetRawText(), a.GetRawText(), $"expected {e.ValueKind} but got {a.ValueKind}"); return; }
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (!a.TryGetProperty(p.Name, out var av)) report($"{path}.{p.Name}", p.Value.GetRawText(), "", "missing key");
                    else Compare($"{path}.{p.Name}", p.Value, av, unordered, report);
                }
                foreach (var p in a.EnumerateObject())
                    if (!e.TryGetProperty(p.Name, out _)) report($"{path}.{p.Name}", "", p.Value.GetRawText(), "unexpected key");
                break;
            case JsonValueKind.Array when unordered:
                if (!Equal(e, a, true)) report(path, e.GetRawText(), a.GetRawText(), "arrays differ (ignoring order)");
                break;
            case JsonValueKind.Array:
                var ea = e.EnumerateArray().ToList();
                var aa = a.EnumerateArray().ToList();
                if (ea.Count != aa.Count) report(path, e.GetRawText(), a.GetRawText(), $"expected {ea.Count} elements but got {aa.Count}");
                for (int i = 0; i < Math.Min(ea.Count, aa.Count); i++) Compare($"{path}[{i}]", ea[i], aa[i], false, report);
                break;
            default:
                if (!Equal(e, a, false)) report(path, e.GetRawText(), a.GetRawText(), $"expected {e.GetRawText()} but got {a.GetRawText()}");
                break;
        }
    }

    private static bool Equal(JsonElement e, JsonElement a, bool unordered)
    {
        if (!SameKind(e, a)) return false;
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var eo = e.EnumerateObject().ToList();
                return eo.Count == a.EnumerateObject().Count()
                    && eo.All(p => a.TryGetProperty(p.Name, out var av) && Equal(p.Value, av, unordered));
            case JsonValueKind.Array:
                var ea = e.EnumerateArray().ToList();
                var aa = a.EnumerateArray().ToList();
                if (ea.Count != aa.Count) return false;
                if (!unordered) return ea.Zip(aa).All(t => Equal(t.First, t.Second, false));
                foreach (var x in ea)
                {
                    var idx = aa.FindIndex(y => Equal(x, y, true));
                    if (idx < 0) return false;
                    aa.RemoveAt(idx);
                }
                return true;
            case JsonValueKind.String: return e.GetString() == a.GetString();
            case JsonValueKind.Number:
                return e.TryGetDecimal(out var d1) && a.TryGetDecimal(out var d2) ? d1 == d2 : e.GetDouble() == a.GetDouble();
            case JsonValueKind.True or JsonValueKind.False: return e.GetBoolean() == a.GetBoolean();
            default: return true; // both null
        }
    }
}
