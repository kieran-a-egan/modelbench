using System.Globalization;
using System.Text.Json;
using ModelBench.Core.Domain;

namespace ModelBench.Core.Grading;

/// <summary>
/// Config: { problem: "knapsack"|"schedule"|"coloring", instance: json, optimum?: number, optimalOnly?: bool }.
/// The answer is a JSON object. Emits `parses`, one assertion per constraint, and `optimal` when optimum is present
/// (the candidate's objective equals the optimum). optimalOnly: true emits only `optimal`, so a family can weight
/// optimality separately from feasibility with two specs.
/// Answer shapes: knapsack {"items":[id,..]}; schedule {"schedule":[{"id":..,"start":..},..]}; coloring {"colors":[c0,..]} (colours 1-based).
/// Objectives: knapsack total value (maximise); schedule makespan (minimise); coloring distinct colours (minimise).
/// </summary>
public sealed class ConstraintGrader : IGrader
{
    public string Type => "constraint";

    private delegate (List<AssertionOutcome> Checks, double? Objective) Checker(JsonElement instance, JsonElement answer);

    private static readonly Dictionary<string, Checker> Checkers = new()
    {
        ["knapsack"] = Knapsack,
        ["schedule"] = Schedule,
        ["coloring"] = Coloring,
    };

    public Task<IReadOnlyList<AssertionOutcome>> GradeAsync(GraderSpec spec, GradingContext ctx)
    {
        var problem = spec.Config.GetProperty("problem").GetString() ?? "";
        if (!Checkers.TryGetValue(problem, out var check)) throw new ArgumentException($"grader '{spec.Name}': unknown problem '{problem}'");
        var instance = spec.Config.GetProperty("instance");
        var optimalOnly = spec.Config.TryGetProperty("optimalOnly", out var oo) && oo.GetBoolean();

        JsonDocument? doc = null;
        var answer = default(JsonElement);
        var parseDiag = ctx.Answer is null ? "no answer" : null;
        if (ctx.Answer is not null)
            try { doc = JsonDocument.Parse(ctx.Answer); answer = doc.RootElement; }
            catch (JsonException ex) { parseDiag = $"answer is not valid JSON: {ex.Message}"; }
        using (doc)
        {
            if (answer.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined)) { parseDiag = "answer is not a JSON object"; answer = default; }
            var (checks, objective) = check(instance, answer);
            var outcomes = new List<AssertionOutcome>();
            if (!optimalOnly)
            {
                outcomes.Add(AssertionOutcome.Check("parses", parseDiag is null, "a JSON object", ctx.Answer, parseDiag));
                outcomes.AddRange(checks);
            }
            if (spec.Config.TryGetProperty("optimum", out var opt) && opt.ValueKind == JsonValueKind.Number)
            {
                var o = opt.GetDouble();
                outcomes.Add(AssertionOutcome.Check("optimal", objective == o, Fmt(o), objective is { } v ? Fmt(v) : null,
                    objective is null ? "the answer is not feasible, so it has no objective" : $"objective {Fmt(objective.Value)} is not the optimum {Fmt(o)}"));
            }
            return Task.FromResult<IReadOnlyList<AssertionOutcome>>(outcomes);
        }
    }

    private static string Fmt(double d) => d.ToString(CultureInfo.InvariantCulture);

    private static AssertionOutcome C(string name, bool ok, string diag) => AssertionOutcome.Check(name, ok, "satisfied", ok ? "satisfied" : "violated", diag);

    /// <summary>The ints of answer[name] when it is an array of integers, else null.</summary>
    private static List<int>? Ints(JsonElement answer, string name)
    {
        if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var list = new List<int>();
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out var i)) return null;
            list.Add(i);
        }
        return list;
    }

    private static (List<AssertionOutcome>, double?) Knapsack(JsonElement inst, JsonElement answer)
    {
        var items = inst.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetInt32());
        var capacity = inst.GetProperty("capacity").GetInt32();
        var ids = Ints(answer, "items");
        var valid = ids is not null && ids.All(items.ContainsKey);
        var unique = ids is not null && ids.Distinct().Count() == ids.Count;
        var weight = valid ? ids!.Sum(i => (long)items[i].GetProperty("weight").GetInt32()) : 0; // long: huge ids/duplicates must not overflow
        var fits = valid && weight <= capacity;
        var checks = new List<AssertionOutcome>
        {
            C("item-ids-valid", valid, "\"items\" must be an array of ids that exist in the instance"),
            C("no-duplicates", unique, "an item id appears more than once"),
            C("capacity", fits, valid ? $"total weight {weight} exceeds capacity {capacity}" : "no valid item list"),
        };
        return (checks, valid && unique && fits ? ids!.Sum(i => (long)items[i].GetProperty("value").GetInt32()) : null);
    }

    private static (List<AssertionOutcome>, double?) Schedule(JsonElement inst, JsonElement answer)
    {
        var tasks = inst.GetProperty("tasks").EnumerateArray()
            .Select(t => (Id: t.GetProperty("id").GetInt32(), R: t.GetProperty("release").GetInt32(), D: t.GetProperty("duration").GetInt32(), Dl: t.GetProperty("deadline").GetInt32()))
            .ToList();
        var entries = new List<(int Id, int Start)>();
        var shapeOk = answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("schedule", out var sch) && sch.ValueKind == JsonValueKind.Array;
        if (shapeOk)
            foreach (var e in answer.GetProperty("schedule").EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var id) && id.TryGetInt32(out var i)
                    && e.TryGetProperty("start", out var st) && st.TryGetInt32(out var s)) entries.Add((i, s));
                else { shapeOk = false; break; }
            }
        var known = tasks.Select(t => t.Id).ToHashSet();
        var idsValid = shapeOk && entries.All(e => known.Contains(e.Id));
        var unique = shapeOk && entries.Select(e => e.Id).Distinct().Count() == entries.Count;
        var complete = idsValid && unique && entries.Count == tasks.Count;
        var start = new Dictionary<int, long>();
        if (idsValid) foreach (var e in entries) start.TryAdd(e.Id, e.Start);

        var checks = new List<AssertionOutcome>
        {
            C("ids-valid", idsValid, "\"schedule\" must be an array of {id,start} with ids that exist in the instance"),
            C("no-duplicates", unique, "a task is scheduled more than once"),
            C("all-scheduled", complete, "every task must be scheduled exactly once"),
        };
        foreach (var t in tasks)
        {
            var has = start.TryGetValue(t.Id, out var s);
            checks.Add(C($"release[{t.Id}]", has && s >= t.R, has ? $"starts at {s}, before release {t.R}" : "not scheduled"));
            checks.Add(C($"deadline[{t.Id}]", has && s + (long)t.D <= t.Dl, has ? $"ends at {s + (long)t.D}, after deadline {t.Dl}" : "not scheduled"));
        }
        for (var a = 0; a < tasks.Count; a++)
            for (var b = a + 1; b < tasks.Count; b++)
            {
                var (x, y) = (tasks[a], tasks[b]);
                var both = start.TryGetValue(x.Id, out var sx) & start.TryGetValue(y.Id, out var sy);
                checks.Add(C($"no-overlap[{x.Id},{y.Id}]", both && (sx + (long)x.D <= sy || sy + (long)y.D <= sx), both ? $"tasks {x.Id} and {y.Id} overlap" : "a task is not scheduled"));
            }
        return (checks, checks.All(c => c.Passed) ? tasks.Max(t => start[t.Id] + (long)t.D) : null);
    }

    private static (List<AssertionOutcome>, double?) Coloring(JsonElement inst, JsonElement answer)
    {
        var n = inst.GetProperty("vertices").GetInt32();
        var k = inst.GetProperty("maxColors").GetInt32();
        var edges = inst.GetProperty("edges").EnumerateArray().Select(e => (U: e[0].GetInt32(), V: e[1].GetInt32())).ToList();
        var colors = Ints(answer, "colors");
        var valid = colors is not null && colors.Count == n && colors.All(c => c >= 1);
        var distinct = valid ? colors!.Distinct().Count() : 0;
        var checks = new List<AssertionOutcome> { C("colors-valid", valid, $"\"colors\" must be {n} integers >= 1, one per vertex") };
        foreach (var (u, v) in edges)
            checks.Add(C($"edge[{u},{v}]", valid && colors![u] != colors[v], valid ? $"vertices {u} and {v} share colour {colors![u]}" : "no valid colouring"));
        checks.Add(C("colors<=k", valid && distinct <= k, valid ? $"uses {distinct} colours, the limit is {k}" : "no valid colouring"));
        return (checks, checks.All(c => c.Passed) ? distinct : null);
    }
}
