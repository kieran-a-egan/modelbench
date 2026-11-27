using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelBench.Core.Domain;
using ModelBench.Core.Generation;

namespace ModelBench.Core.Families;

/// <summary>
/// Seeded table (5..200 rows) plus a prose transformation: filter, group-by, aggregate, sort (total order), limit.
/// Expected output is a JSON array computed with LINQ; Validate recomputes it with plain loops and integer arithmetic.
/// </summary>
public sealed class DataTransformationFamily : IBenchmarkFamily
{
    public string Id => "data-transformation";
    public int Version => 2; // 2: prompt says the sort uses the rounded average as reported (issue #15)
    public string Category => "data-transformation";
    public string Description => "Apply a filter, group-by, aggregate, sort and limit described in prose to a CSV/JSON table.";
    public ExecutionMode Mode => ExecutionMode.SingleTurn;

    private sealed record Schema(string Table, string Id, string Cat1, string[] V1, string Cat2, string[] V2, string Num1, int Lo1, int Hi1, string Num2, int Lo2, int Hi2);

    private static readonly Schema[] Schemas =
    [
        new("employees", "id", "dept", ["Sales", "Support", "Engineering", "Finance", "Legal"], "city", ["Leeds", "Bristol", "Cardiff", "Derby"], "salary", 20000, 90000, "age", 21, 64),
        new("orders", "id", "region", ["North", "South", "East", "West"], "product", ["Widget", "Gadget", "Gizmo", "Doohickey", "Sprocket"], "qty", 1, 20, "price", 5, 500),
        new("readings", "id", "site", ["Alpha", "Beta", "Gamma", "Delta"], "kind", ["temp", "humidity", "pressure"], "reading", 0, 400, "battery", 1, 100),
    ];

    private static readonly int[] Rows = [6, 20, 50, 100, 200];

    private sealed record Filter(string Col, string Op, string Value);
    private sealed record Agg(string Fn, string? Col) { public string Name => Col is null ? "count" : $"{Fn}_{Col}"; }
    private sealed record Spec(string Table, string[] Cols, string[] NumCols, string[][] Rows, Filter[] Filters, string[] GroupBy, Agg[] Aggs, string SortBy, bool Desc, int Limit);

    public GeneratedTask Generate(Rng rng, int c)
    {
        if (c is < Complexity.Min or > Complexity.Max) throw new ArgumentOutOfRangeException(nameof(c));
        while (true)
        {
            var s = rng.Pick(Schemas);
            string[] cols = [s.Id, s.Cat1, s.Cat2, s.Num1, s.Num2];
            var rows = Enumerable.Range(1, Rows[c - 1]).Select(i => new[]
            {
                $"{s.Table[0]}{i:000}", rng.Pick(s.V1), rng.Pick(s.V2), rng.Range(s.Lo1, s.Hi1).ToString(), rng.Range(s.Lo2, s.Hi2).ToString(),
            }).ToArray();

            var filters = new List<Filter>();
            var nf = c < 2 ? 0 : c < 4 ? 1 : 2;
            var cands = new List<string> { s.Num1, s.Num2, s.Cat1, s.Cat2 };
            rng.Shuffle(cands);
            foreach (var col in cands.Take(nf))
            {
                var idx = Array.IndexOf(cols, col);
                var v = rng.Pick(rows)[idx];
                filters.Add(col == s.Cat1 || col == s.Cat2 ? new Filter(col, rng.Chance(0.5) ? "==" : "!=", v) : new Filter(col, rng.Pick(new[] { ">=", "<=", ">", "<" }), v));
            }
            var aggPool = new List<Agg> { new("sum", s.Num1), new("count", null), new("avg", s.Num2), new("min", s.Num1), new("max", s.Num2) };
            rng.Shuffle(aggPool);
            var aggs = aggPool.Take(c < 3 ? 1 : c < 4 ? 2 : 3).ToArray();
            var spec = new Spec(s.Table, cols, [s.Num1, s.Num2], rows, filters.ToArray(),
                c < 5 ? [s.Cat1] : [s.Cat1, s.Cat2], aggs, rng.Pick(aggs).Name, rng.Chance(0.5), c < 3 ? 0 : rng.Range(2, 4));

            var expected = Compute(spec);
            if (expected.Count < 2) continue; // resample from the same rng
            return Build(spec, expected, rng.Chance(0.5));
        }
    }

    private static int Col(Spec s, string name) => Array.IndexOf(s.Cols, name);

    private static bool Match(Spec s, string[] row, Filter f)
    {
        var v = row[Col(s, f.Col)];
        if (!s.NumCols.Contains(f.Col)) return (v == f.Value) == (f.Op == "==");
        var (a, b) = (long.Parse(v), long.Parse(f.Value));
        return f.Op switch { ">=" => a >= b, "<=" => a <= b, ">" => a > b, _ => a < b };
    }

    /// <summary>LINQ reference implementation.</summary>
    private static JsonArray Compute(Spec s)
    {
        var groups = s.Rows.Where(r => s.Filters.All(f => Match(s, r, f)))
            .GroupBy(r => string.Join("\u0001", s.GroupBy.Select(g => r[Col(s, g)])))
            .Select(g => (Keys: s.GroupBy.Select(k => g.First()[Col(s, k)]).ToArray(), Rows: g.ToList()));
        var objs = groups.Select(g =>
        {
            var o = new JsonObject();
            for (var i = 0; i < s.GroupBy.Length; i++) o[s.GroupBy[i]] = g.Keys[i];
            decimal sortVal = 0;
            foreach (var a in s.Aggs)
            {
                var vals = a.Col is null ? [] : g.Rows.Select(r => long.Parse(r[Col(s, a.Col)])).ToList();
                decimal v = a.Fn switch
                {
                    "sum" => vals.Sum(),
                    "count" => g.Rows.Count,
                    "min" => vals.Min(),
                    "max" => vals.Max(),
                    _ => Math.Round((decimal)vals.Sum() / vals.Count, 2, MidpointRounding.AwayFromZero),
                };
                o[a.Name] = a.Fn == "avg" ? JsonValue.Create(v) : JsonValue.Create((long)v);
                if (a.Name == s.SortBy) sortVal = v;
            }
            return (O: o, Keys: g.Keys, SortVal: sortVal);
        });
        var ordered = s.Desc ? objs.OrderByDescending(t => t.SortVal) : objs.OrderBy(t => t.SortVal);
        for (var k = 0; k < s.GroupBy.Length; k++)
        {
            var kk = k;
            ordered = ordered.ThenBy(t => t.Keys[kk], StringComparer.Ordinal);
        }
        var list = s.Limit > 0 ? ordered.Take(s.Limit) : ordered;
        return new JsonArray(list.Select(t => (JsonNode)t.O).ToArray());
    }

    private GeneratedTask Build(Spec s, JsonArray expected, bool asJson)
    {
        var sb = new StringBuilder($"You are given a table named `{s.Table}` as {(asJson ? "JSON" : "CSV")}.\n\n");
        if (asJson)
            sb.Ln(new JsonArray(s.Rows.Select(r =>
            {
                var o = new JsonObject();
                for (var i = 0; i < s.Cols.Length; i++) o[s.Cols[i]] = s.NumCols.Contains(s.Cols[i]) ? JsonValue.Create(long.Parse(r[i])) : JsonValue.Create(r[i]);
                return (JsonNode)o;
            }).ToArray()).ToJsonString());
        else
        {
            sb.Ln(string.Join(",", s.Cols));
            foreach (var r in s.Rows) sb.Ln(string.Join(",", r));
        }
        sb.Ln().Ln("Transform it as follows, in this order:");
        var n = 1;
        if (s.Filters.Length > 0)
            sb.Ln($"{n++}. Keep only the rows where " + string.Join(" and ", s.Filters.Select(f => $"{f.Col} {f.Op} {(s.NumCols.Contains(f.Col) ? f.Value : $"\"{f.Value}\"")}")) + ".");
        sb.Ln($"{n++}. Group the remaining rows by {string.Join(" and ", s.GroupBy)}.");
        sb.Ln($"{n++}. For each group compute: " + string.Join("; ", s.Aggs.Select(a => a.Fn switch
        {
            "count" => "count = the number of rows",
            "avg" => $"{a.Name} = the mean of {a.Col}, rounded to 2 decimal places (half away from zero)",
            _ => $"{a.Name} = the {(a.Fn == "sum" ? "sum" : a.Fn == "min" ? "minimum" : "maximum")} of {a.Col}",
        })) + ".");
        sb.Ln($"{n++}. Sort the groups by {s.SortBy} {(s.Desc ? "descending" : "ascending")}, comparing the values exactly as reported (averages already rounded); break ties by " + string.Join(", then ", s.GroupBy.Select(g => $"{g} ascending (plain character-code order)")) + ".");
        if (s.Limit > 0) sb.Ln($"{n}. Keep only the first {s.Limit} groups.");
        var keys = s.GroupBy.Concat(s.Aggs.Select(a => a.Name)).ToArray();
        sb.Ln().Append($"Reply with a JSON array of objects, one per group in the sorted order, each with exactly the keys {string.Join(", ", keys)} ")
            .Append("(numbers as JSON numbers, group values as strings), inside <answer></answer> tags. If you use code fences, still put them inside the tags.");

        var schema = new
        {
            type = "array",
            items = new
            {
                type = "object",
                required = keys,
                additionalProperties = false,
                properties = keys.ToDictionary(k => k, k => new { type = s.GroupBy.Contains(k) ? "string" : k.StartsWith("avg") ? "number" : "integer" }),
            },
        };
        var json = expected.ToJsonString();
        var hidden = new TaskHidden(
            [new GraderSpec("json-schema", "shape", JsonUtil.Element(new { schema })),
             new GraderSpec("structural", "values", JsonUtil.Element(new { expected = JsonUtil.Deserialize<JsonElement>(json) }))],
            null, JsonUtil.Element(s));
        var wrongOrder = new JsonArray(expected.Reverse().Select(x => x!.DeepClone()).ToArray());
        var dropped = new JsonArray(expected.Skip(1).Select(x => x!.DeepClone()).ToArray());
        var renamed = json.Replace($"\"{keys[^1]}\"", "\"value\"");
        return new GeneratedTask(TaskPrompt.Simple(sb.ToString().Replace("\r\n", "\n"), AnswerFormat.Json), hidden, Turn($"<answer>{json}</answer>"),
            [Turn($"<answer>{wrongOrder.ToJsonString()}</answer>"), Turn($"<answer>{dropped.ToJsonString()}</answer>"), Turn($"<answer>{renamed}</answer>"), Turn(json)]);
    }

    private static IReadOnlyList<ChatMessage> Turn(string s) => [new ChatMessage(Role.Assistant, s)];

    // ---------------------------------------------------------------- independent validation

    public ValidationResult Validate(GeneratedTask task)
    {
        var problems = new List<string>();
        try
        {
            if (task.Prompt.AnswerFormat != AnswerFormat.Json) problems.Add("answer format must be Json");
            if (!task.Prompt.User.Contains("<answer></answer>")) problems.Add("prompt does not state the answer format");
            var s = JsonUtil.Deserialize<Spec>(task.Hidden.Reference!.Value.GetRawText());
            var expected = task.Hidden.Graders.Single(g => g.Type == "structural").Config.GetProperty("expected").GetRawText();
            var independent = Naive(s);
            if (!SameJson(expected, independent)) problems.Add($"expected {expected} != independent {independent}");
            if (!task.Prompt.User.Contains($"table named `{s.Table}`")) problems.Add("prompt does not name the table");
            if (s.Rows.Length is < 5 or > 200) problems.Add("row count outside 5..200");
            foreach (var r in s.Rows.Take(3))
                if (!task.Prompt.User.Contains(string.Join(",", r)) && !task.Prompt.User.Contains($"\"{s.Cols[0]}\":\"{r[0]}\"")) problems.Add("prompt does not show the data");
            var refAnswer = Answer(task.ReferenceTurns.Single().Content);
            if (refAnswer is null || !SameJson(refAnswer, independent)) problems.Add("reference turn does not match the independent result");
            if (task.KnownBadTurns.Count < 2) problems.Add("need at least two known-bad turns");
            foreach (var bad in task.KnownBadTurns)
                if (Answer(bad.Last().Content) is { } a && SameJson(a, independent)) problems.Add("a known-bad turn is correct");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            problems.Add($"malformed task: {ex.Message}");
        }
        return problems.Count == 0 ? ValidationResult.Valid : new ValidationResult(false, problems);
    }

    private static string? Answer(string? text)
    {
        if (text is null) return null;
        var a = text.LastIndexOf("<answer>", StringComparison.Ordinal);
        var b = text.LastIndexOf("</answer>", StringComparison.Ordinal);
        return a < 0 || b < a ? null : text[(a + 8)..b].Trim();
    }

    private static bool SameJson(string a, string b)
    {
        using var x = JsonDocument.Parse(a);
        using var y = JsonDocument.Parse(b);
        return Eq(x.RootElement, y.RootElement);
    }

    private static bool Eq(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        return a.ValueKind switch
        {
            JsonValueKind.Array => a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray()).All(t => Eq(t.First, t.Second)),
            JsonValueKind.Object => a.EnumerateObject().Count() == b.EnumerateObject().Count()
                && a.EnumerateObject().All(p => b.TryGetProperty(p.Name, out var q) && Eq(p.Value, q)),
            JsonValueKind.Number => a.GetDecimal() == b.GetDecimal(),
            _ => a.GetRawText() == b.GetRawText(),
        };
    }

    /// <summary>Naive loops with integer arithmetic (averages in hundredths); returns a JSON array text.</summary>
    private static string Naive(Spec s)
    {
        var keysSeen = new List<string>();
        var members = new List<List<string[]>>();
        foreach (var r in s.Rows)
        {
            var keep = true;
            foreach (var f in s.Filters)
            {
                var v = r[Array.IndexOf(s.Cols, f.Col)];
                bool ok;
                if (f.Op is "==" or "!=") ok = (v == f.Value) == (f.Op == "==");
                else
                {
                    var cmp = long.Parse(v).CompareTo(long.Parse(f.Value));
                    ok = f.Op == ">=" ? cmp >= 0 : f.Op == "<=" ? cmp <= 0 : f.Op == ">" ? cmp > 0 : cmp < 0;
                }
                if (!ok) keep = false;
            }
            if (!keep) continue;
            var key = string.Join("|", s.GroupBy.Select(g => r[Array.IndexOf(s.Cols, g)]));
            var gi = keysSeen.IndexOf(key);
            if (gi < 0) { keysSeen.Add(key); members.Add([]); gi = keysSeen.Count - 1; }
            members[gi].Add(r);
        }
        // each result row: (group values, aggregate values in hundredths)
        var result = new List<(string[] Keys, long[] Hundredths)>();
        for (var g = 0; g < members.Count; g++)
        {
            var aggs = new long[s.Aggs.Length];
            for (var ai = 0; ai < s.Aggs.Length; ai++)
            {
                var a = s.Aggs[ai];
                if (a.Fn == "count") { aggs[ai] = members[g].Count * 100L; continue; }
                var ci = Array.IndexOf(s.Cols, a.Col);
                long sum = 0, mn = long.MaxValue, mx = long.MinValue;
                foreach (var r in members[g]) { var v = long.Parse(r[ci]); sum += v; mn = Math.Min(mn, v); mx = Math.Max(mx, v); }
                var n = members[g].Count;
                aggs[ai] = a.Fn switch
                {
                    "sum" => sum * 100, "min" => mn * 100, "max" => mx * 100,
                    _ => sum >= 0 ? (2 * sum * 100 + n) / (2 * n) : -((2 * -sum * 100 + n) / (2 * n)), // half away from zero
                };
            }
            result.Add((s.GroupBy.Select(k => members[g][0][Array.IndexOf(s.Cols, k)]).ToArray(), aggs));
        }
        var sortIdx = Array.FindIndex(s.Aggs, a => a.Name == s.SortBy);
        // insertion sort with an explicit comparison
        for (var i = 1; i < result.Count; i++)
            for (var j = i; j > 0 && Before(result[j], result[j - 1]); j--) (result[j], result[j - 1]) = (result[j - 1], result[j]);
        bool Before((string[] Keys, long[] H) x, (string[] Keys, long[] H) y)
        {
            if (x.H[sortIdx] != y.H[sortIdx]) return s.Desc ? x.H[sortIdx] > y.H[sortIdx] : x.H[sortIdx] < y.H[sortIdx];
            for (var k = 0; k < x.Keys.Length; k++)
            {
                var c = string.CompareOrdinal(x.Keys[k], y.Keys[k]);
                if (c != 0) return c < 0;
            }
            return false;
        }
        if (s.Limit > 0 && result.Count > s.Limit) result.RemoveRange(s.Limit, result.Count - s.Limit);
        var sb = new StringBuilder("[");
        for (var i = 0; i < result.Count; i++)
        {
            sb.Append(i > 0 ? "," : "").Append('{');
            for (var k = 0; k < s.GroupBy.Length; k++) sb.Append($"{(k > 0 ? "," : "")}\"{s.GroupBy[k]}\":\"{result[i].Keys[k]}\"");
            for (var a = 0; a < s.Aggs.Length; a++)
            {
                var h = result[i].Hundredths[a];
                sb.Append($",\"{s.Aggs[a].Name}\":{(h < 0 ? "-" : "")}{Math.Abs(h) / 100}.{Math.Abs(h) % 100:00}");
            }
            sb.Append('}');
        }
        return sb.Append(']').ToString();
    }
}
