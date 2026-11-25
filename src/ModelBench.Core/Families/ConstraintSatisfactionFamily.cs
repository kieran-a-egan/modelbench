using System.Text;
using System.Text.Json;
using ModelBench.Core.Domain;
using ModelBench.Core.Generation;
using ModelBench.Core.Grading;

namespace ModelBench.Core.Families;

/// <summary>
/// Seeded optimisation puzzles with an exact optimum: 0/1 knapsack, single-machine scheduling with release times and
/// deadlines (minimise makespan) and graph colouring (minimise colours). Answers are JSON graded by the `constraint` grader.
/// Complexity scales instance size (knapsack 5..25 items, schedule 4..8 tasks, colouring 5..14 vertices).
/// </summary>
public sealed class ConstraintSatisfactionFamily : IBenchmarkFamily
{
    public string Id => "constraint-satisfaction";
    public int Version => 1;
    public string Category => "constraint-satisfaction";
    public string Description => "Knapsack, deadline scheduling and graph colouring with a provably optimal reference answer.";
    public ExecutionMode Mode => ExecutionMode.SingleTurn;

    private static readonly string[] Problems = ["knapsack", "schedule", "coloring"];
    private static readonly int[] ColoringSizes = [5, 7, 9, 11, 14];

    // ---------------------------------------------------------------- generation

    public GeneratedTask Generate(Rng rng, int c)
    {
        if (c is < Complexity.Min or > Complexity.Max) throw new ArgumentOutOfRangeException(nameof(c));
        var problem = Problems[rng.Next(3)];
        // instance JSON, optimum, reference answer object, plus known-bad answer objects
        var (instance, optimum, reference, bad) = problem switch
        {
            "knapsack" => GenKnapsack(rng, c),
            "schedule" => GenSchedule(rng, c),
            _ => GenColoring(rng, c),
        };
        var user = Render(problem, instance);
        var hidden = new TaskHidden(
            [new GraderSpec("constraint", "feasibility", JsonUtil.Element(new { problem, instance }), 3),
             new GraderSpec("constraint", "optimality", JsonUtil.Element(new { problem, instance, optimum, optimalOnly = true }), 2)],
            null, JsonUtil.Element(new { problem, optimum }));
        var turns = new List<IReadOnlyList<ChatMessage>>();
        foreach (var b in bad) turns.Add(Turn($"Here is my answer.\n<answer>{JsonUtil.Serialize(b)}</answer>"));
        turns.Add(Turn("I think the best choice is to take the cheapest options first, but I did not finish."));
        return new GeneratedTask(TaskPrompt.Simple(user, AnswerFormat.Json), hidden,
            [new ChatMessage(Role.Assistant, $"Working...\n<answer>{JsonUtil.Serialize(reference)}</answer>")], turns);
    }

    private static IReadOnlyList<ChatMessage> Turn(string text) => [new ChatMessage(Role.Assistant, text)];

    private static (JsonElement, int, object, object[]) GenKnapsack(Rng rng, int c)
    {
        var n = 5 + 5 * (c - 1);
        var weights = new int[n];
        var values = new int[n];
        for (var i = 0; i < n; i++)
        {
            weights[i] = rng.Range(3, 30);
            values[i] = Math.Max(1, weights[i] + rng.Range(-2, 12));
        }
        var cap = weights.Sum() / 2;
        // classic DP indexed by capacity, with reconstruction
        var dp = new int[n + 1, cap + 1];
        for (var i = 1; i <= n; i++)
            for (var w = 0; w <= cap; w++)
            {
                dp[i, w] = dp[i - 1, w];
                if (w >= weights[i - 1]) dp[i, w] = Math.Max(dp[i, w], dp[i - 1, w - weights[i - 1]] + values[i - 1]);
            }
        var chosen = new List<int>();
        for (int i = n, w = cap; i >= 1; i--)
            if (dp[i, w] != dp[i - 1, w]) { chosen.Add(i); w -= weights[i - 1]; }
        chosen.Sort();
        var instance = JsonUtil.Element(new
        {
            capacity = cap,
            items = Enumerable.Range(0, n).Select(i => new { id = i + 1, weight = weights[i], value = values[i] }).ToArray(),
        });
        var smallest = chosen.MinBy(id => values[id - 1]);
        return (instance, dp[n, cap], new { items = chosen },
            [new { items = Enumerable.Range(1, n).ToArray() }, new { items = chosen.Where(id => id != smallest).ToArray() }]);
    }

    private static (JsonElement, int, object, object[]) GenSchedule(Rng rng, int c)
    {
        var n = c + 3;
        var dur = new int[n];
        for (var i = 0; i < n; i++) dur[i] = rng.Range(1, 3 + c);
        var order = Enumerable.Range(0, n).ToList();
        rng.Shuffle(order);
        // build a feasible hidden order first, so the instance always has a solution
        var rel = new int[n];
        var dl = new int[n];
        var t = 0;
        foreach (var j in order)
        {
            rel[j] = rng.Range(0, dur.Sum() / 2 + 2);
            t = Math.Max(t, rel[j]) + dur[j];
            dl[j] = t + rng.Range(0, Math.Max(1, 7 - c));
        }
        // exhaustive search over orders; each order is run as early as possible, which is optimal for a fixed order
        int best = int.MaxValue;
        var bestOrder = new int[n];
        var cur = new int[n];
        var used = new bool[n];
        void Rec(int k, int time)
        {
            if (k == n) { best = time; Array.Copy(cur, bestOrder, n); return; }
            for (var j = 0; j < n; j++)
            {
                if (used[j]) continue;
                var end = Math.Max(time, rel[j]) + dur[j];
                if (end > dl[j] || end >= best) continue;
                used[j] = true; cur[k] = j;
                Rec(k + 1, end);
                used[j] = false;
            }
        }
        Rec(0, 0);
        var starts = new int[n];
        var clock = 0;
        foreach (var j in bestOrder) { starts[j] = Math.Max(clock, rel[j]); clock = starts[j] + dur[j]; }
        var instance = JsonUtil.Element(new
        {
            tasks = Enumerable.Range(0, n).Select(i => new { id = i + 1, release = rel[i], duration = dur[i], deadline = dl[i] }).ToArray(),
        });
        object Sched(Func<int, int> start) => new { schedule = Enumerable.Range(0, n).Select(i => new { id = i + 1, start = start(i) }).ToArray() };
        var last = bestOrder[n - 1];
        return (instance, best, Sched(i => starts[i]),
            [Sched(_ => 0), Sched(i => starts[i] + (i == last ? 1 : 0))]);
    }

    private static (JsonElement, int, object, object[]) GenColoring(Rng rng, int c)
    {
        var n = ColoringSizes[c - 1];
        var p = 0.25 + 0.06 * c;
        var edges = new List<int[]>();
        for (var u = 0; u < n; u++)
            for (var v = u + 1; v < n; v++)
                if (rng.Chance(p)) edges.Add([u, v]);
        if (edges.Count == 0) edges.Add([0, 1]);
        var adj = new bool[n, n];
        foreach (var e in edges) adj[e[0], e[1]] = adj[e[1], e[0]] = true;
        var maxDeg = Enumerable.Range(0, n).Max(u => Enumerable.Range(0, n).Count(v => adj[u, v]));

        // backtracking: try k = 1, 2, ... ; a vertex may open at most one new colour (symmetry breaking)
        var col = new int[n];
        bool Try(int v, int k, int used)
        {
            if (v == n) return true;
            for (var x = 1; x <= Math.Min(k, used + 1); x++)
            {
                if (Enumerable.Range(0, v).Any(u => adj[u, v] && col[u] == x)) continue;
                col[v] = x;
                if (Try(v + 1, k, Math.Max(used, x))) return true;
            }
            return false;
        }
        var chi = 1;
        while (!Try(0, chi, 0)) chi++;
        var instance = JsonUtil.Element(new { vertices = n, edges, maxColors = Math.Min(n, maxDeg + 1) });
        var recoloured = (int[])col.Clone();
        recoloured[0] = chi + 1;
        // a fresh colour for vertex 0 only costs a colour when vertex 0 shared its colour; otherwise send a short array instead
        var second = recoloured.Distinct().Count() > chi ? recoloured : col[..^1];
        return (instance, chi, new { colors = col.ToArray() },
            [new { colors = new int[n].Select(_ => 1).ToArray() }, new { colors = second }]);
    }

    // ---------------------------------------------------------------- prompt

    private static string Render(string problem, JsonElement inst)
    {
        var sb = new StringBuilder();
        switch (problem)
        {
            case "knapsack":
                sb.AppendLine($"You have a knapsack with capacity {inst.GetProperty("capacity").GetInt32()}. Choose a set of items with the largest possible total value whose total weight does not exceed the capacity. Each item can be used at most once.").AppendLine()
                  .AppendLine("| id | weight | value |").AppendLine("|---|---|---|");
                foreach (var i in inst.GetProperty("items").EnumerateArray())
                    sb.AppendLine($"| {i.GetProperty("id").GetInt32()} | {i.GetProperty("weight").GetInt32()} | {i.GetProperty("value").GetInt32()} |");
                sb.AppendLine().Append("Give the chosen item ids inside <answer></answer> tags as a JSON object, exactly in this form: <answer>{\"items\":[1,4,7]}</answer>");
                break;
            case "schedule":
                sb.AppendLine("A single machine runs one task at a time and cannot interrupt a task. Each task has a release time (it cannot start earlier), a duration and a deadline (it must finish no later than this). Start times are integers. "
                    + "Schedule every task exactly once so that the makespan, the time at which the last task finishes, is as small as possible.").AppendLine()
                  .AppendLine("| id | release | duration | deadline |").AppendLine("|---|---|---|---|");
                foreach (var t in inst.GetProperty("tasks").EnumerateArray())
                    sb.AppendLine($"| {t.GetProperty("id").GetInt32()} | {t.GetProperty("release").GetInt32()} | {t.GetProperty("duration").GetInt32()} | {t.GetProperty("deadline").GetInt32()} |");
                sb.AppendLine().Append("Give the schedule inside <answer></answer> tags as a JSON object, exactly in this form: <answer>{\"schedule\":[{\"id\":1,\"start\":0},{\"id\":2,\"start\":5}]}</answer>");
                break;
            default:
                var n = inst.GetProperty("vertices").GetInt32();
                sb.AppendLine($"An undirected graph has vertices 0 to {n - 1} and the edges listed below. Colour every vertex with a positive integer so that the two ends of every edge get different colours, using as few distinct colours as possible. "
                    + $"You may use at most {inst.GetProperty("maxColors").GetInt32()} distinct colours.").AppendLine()
                  .AppendLine("Edges: " + string.Join(", ", inst.GetProperty("edges").EnumerateArray().Select(e => $"({e[0].GetInt32()},{e[1].GetInt32()})"))).AppendLine()
                  .Append($"Give the colours inside <answer></answer> tags as a JSON object whose \"colors\" array has {n} integers, where element i is the colour of vertex i, exactly in this form: <answer>{{\"colors\":[1,2,1]}}</answer>");
                break;
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- independent validation

    public ValidationResult Validate(GeneratedTask task)
    {
        var problems = new List<string>();
        try
        {
            if (task.Prompt.AnswerFormat != AnswerFormat.Json) problems.Add("answer format must be Json");
            var specs = task.Hidden.Graders.Where(g => g.Type == "constraint").ToList();
            var feas = specs.Single(g => !g.Config.TryGetProperty("optimalOnly", out _));
            var optSpec = specs.Single(g => g.Config.TryGetProperty("optimalOnly", out _));
            if (!optSpec.Required || !feas.Required) problems.Add("constraint specs must be required");
            var problem = feas.Config.GetProperty("problem").GetString()!;
            var inst = feas.Config.GetProperty("instance");
            if (optSpec.Config.GetProperty("instance").GetRawText() != inst.GetRawText()) problems.Add("graders disagree on the instance");
            if (!task.Prompt.User.Contains(Render(problem, inst))) problems.Add("prompt does not describe the instance");

            var independent = IndependentOptimum(problem, inst);
            var stated = optSpec.Config.GetProperty("optimum").GetInt32();
            if (stated != independent) problems.Add($"optimum {stated} != independent {independent}");

            var refObjective = Objective(problem, inst, Extract(task.ReferenceTurns.Single().Content));
            if (refObjective != independent) problems.Add($"reference answer objective {(refObjective?.ToString() ?? "infeasible")} != optimum {independent}");
            if (task.KnownBadTurns.Count < 2) problems.Add("need at least two known-bad turns");
            foreach (var bad in task.KnownBadTurns)
            {
                var text = bad.Last().Content;
                if (Extract(text) is { } ans && Objective(problem, inst, ans) == independent) problems.Add("a known-bad turn is feasible and optimal");
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            problems.Add($"malformed task: {ex.Message}");
        }
        return problems.Count == 0 ? ValidationResult.Valid : new ValidationResult(false, problems);
    }

    private static JsonElement? Extract(string? text)
    {
        var a = AnswerExtractor.Extract(text is null ? null : AnswerExtractor.StripReasoning(text), AnswerFormat.Json);
        return a is null ? null : JsonDocument.Parse(a).RootElement.Clone();
    }

    /// <summary>The objective of a candidate answer when it is feasible, else null. Written independently of ConstraintGrader.</summary>
    private static int? Objective(string problem, JsonElement inst, JsonElement? answer)
    {
        if (answer is not { ValueKind: JsonValueKind.Object } a) return null;
        switch (problem)
        {
            case "knapsack":
            {
                if (!a.TryGetProperty("items", out var arr)) return null;
                var table = inst.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetInt32(), i => (W: i.GetProperty("weight").GetInt32(), V: i.GetProperty("value").GetInt32()));
                int w = 0, v = 0;
                var seen = new List<int>();
                foreach (var e in arr.EnumerateArray())
                {
                    var id = e.GetInt32();
                    if (!table.TryGetValue(id, out var it) || seen.Contains(id)) return null;
                    seen.Add(id); w += it.W; v += it.V;
                }
                return w <= inst.GetProperty("capacity").GetInt32() ? v : null;
            }
            case "schedule":
            {
                var jobs = inst.GetProperty("tasks").EnumerateArray().ToList();
                var placed = a.GetProperty("schedule").EnumerateArray().Select(e => (Id: e.GetProperty("id").GetInt32(), S: e.GetProperty("start").GetInt32())).OrderBy(p => p.S).ToList();
                if (placed.Count != jobs.Count || placed.Select(p => p.Id).Distinct().Count() != jobs.Count) return null;
                var prevEnd = int.MinValue;
                var makespan = 0;
                foreach (var (id, s) in placed)
                {
                    var j = jobs.SingleOrDefault(x => x.GetProperty("id").GetInt32() == id);
                    if (j.ValueKind != JsonValueKind.Object) return null;
                    var end = s + j.GetProperty("duration").GetInt32();
                    if (s < j.GetProperty("release").GetInt32() || end > j.GetProperty("deadline").GetInt32() || s < prevEnd) return null;
                    prevEnd = end; makespan = Math.Max(makespan, end);
                }
                return makespan;
            }
            default:
            {
                var colours = a.GetProperty("colors").EnumerateArray().Select(e => e.GetInt32()).ToArray();
                if (colours.Length != inst.GetProperty("vertices").GetInt32() || colours.Any(x => x < 1)) return null;
                if (inst.GetProperty("edges").EnumerateArray().Any(e => colours[e[0].GetInt32()] == colours[e[1].GetInt32()])) return null;
                var k = colours.Distinct().Count();
                return k <= inst.GetProperty("maxColors").GetInt32() ? k : null;
            }
        }
    }

    /// <summary>Re-solves by a different exact method from the generator: value-indexed DP, subset DP, independent-set cover DP.</summary>
    private static int IndependentOptimum(string problem, JsonElement inst)
    {
        switch (problem)
        {
            case "knapsack":
            {
                var items = inst.GetProperty("items").EnumerateArray().Select(i => (W: i.GetProperty("weight").GetInt32(), V: i.GetProperty("value").GetInt32())).ToList();
                var cap = inst.GetProperty("capacity").GetInt32();
                var minWeight = new int[items.Sum(i => i.V) + 1]; // minWeight[v] = least weight reaching value exactly v
                Array.Fill(minWeight, int.MaxValue / 2);
                minWeight[0] = 0;
                foreach (var (w, v) in items)
                    for (var x = minWeight.Length - 1; x >= v; x--) minWeight[x] = Math.Min(minWeight[x], minWeight[x - v] + w);
                return Enumerable.Range(0, minWeight.Length).Last(v => minWeight[v] <= cap);
            }
            case "schedule":
            {
                var jobs = inst.GetProperty("tasks").EnumerateArray().Select(t => (R: t.GetProperty("release").GetInt32(), D: t.GetProperty("duration").GetInt32(), L: t.GetProperty("deadline").GetInt32())).ToList();
                var n = jobs.Count;
                var f = new int[1 << n]; // earliest possible finish time of the subset, or MaxValue when infeasible
                Array.Fill(f, int.MaxValue);
                f[0] = 0;
                for (var mask = 1; mask < 1 << n; mask++)
                    for (var j = 0; j < n; j++)
                    {
                        if ((mask & (1 << j)) == 0 || f[mask ^ (1 << j)] == int.MaxValue) continue;
                        var end = Math.Max(f[mask ^ (1 << j)], jobs[j].R) + jobs[j].D;
                        if (end <= jobs[j].L) f[mask] = Math.Min(f[mask], end);
                    }
                return f[(1 << n) - 1] == int.MaxValue ? throw new InvalidOperationException("instance has no feasible schedule") : f[(1 << n) - 1];
            }
            default:
            {
                var n = inst.GetProperty("vertices").GetInt32();
                var nb = new int[n];
                foreach (var e in inst.GetProperty("edges").EnumerateArray())
                {
                    nb[e[0].GetInt32()] |= 1 << e[1].GetInt32();
                    nb[e[1].GetInt32()] |= 1 << e[0].GetInt32();
                }
                var full = (1 << n) - 1;
                var cover = new int[full + 1]; // fewest independent sets covering the subset
                for (var s = 1; s <= full; s++)
                {
                    var low = s & -s;
                    cover[s] = int.MaxValue;
                    for (var sub = s; sub > 0; sub = (sub - 1) & s)
                    {
                        if ((sub & low) == 0 || !IsIndependent(sub, nb)) continue;
                        cover[s] = Math.Min(cover[s], cover[s ^ sub] + 1);
                    }
                }
                return cover[full];
            }
        }
    }

    private static bool IsIndependent(int set, int[] nb)
    {
        for (var rest = set; rest != 0; rest &= rest - 1)
            if ((nb[System.Numerics.BitOperations.TrailingZeroCount(rest)] & set) != 0) return false;
        return true;
    }
}
