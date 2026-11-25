using System.Text;
using System.Text.Json;
using ModelBench.Core.Domain;
using ModelBench.Core.Generation;

namespace ModelBench.Core.Families;

/// <summary>
/// Seeded logic-grid puzzles. N people stand in a row; attribute 0 is the name, attribute 1 the position (1..N) and the rest are
/// categorical. Complexity c gives N x M of 3x3, 3x4, 4x4, 4x5, 5x5. Clues are added until a backtracking solver finds exactly one
/// solution, then redundant clues are pruned. Answer: {"Name":{"position":"2","drink":"tea",..},..}, all values strings.
/// </summary>
public sealed class DeductiveReasoningFamily : IBenchmarkFamily
{
    public string Id => "deductive-reasoning";
    public int Version => 1;
    public string Category => "deductive-reasoning";
    public string Description => "Logic-grid puzzles with a unique solution, from 3x3 to 5x5.";
    public ExecutionMode Mode => ExecutionMode.SingleTurn;

    /// <summary>Kind: same | not-same | left | next | either. A, B, C are items [attribute, value index].</summary>
    private sealed record Clue(string Kind, int[] A, int[] B, int[]? C = null);
    private sealed record Attr(string Key, string Verb, string[] Values);
    private sealed record Ref(Attr[] Attrs, Clue[] Clues);

    private static readonly string[] Names = ["Alice", "Bob", "Carol", "Dave", "Erin", "Frank", "Grace"];
    private static readonly Attr[] Pool =
    [
        new("drink", "drinks {v}", ["tea", "coffee", "milk", "juice", "water", "soda"]),
        new("pet", "owns the {v}", ["cat", "dog", "bird", "fish", "rabbit", "hamster"]),
        new("hobby", "enjoys {v}", ["chess", "painting", "hiking", "knitting", "cycling", "baking"]),
        new("colour", "wears {v}", ["red", "blue", "green", "yellow", "purple", "orange"]),
        new("food", "eats {v}", ["pasta", "curry", "salad", "pizza", "soup", "tacos"]),
    ];

    // ---------------------------------------------------------------- generation

    public GeneratedTask Generate(Rng rng, int c)
    {
        if (c is < Complexity.Min or > Complexity.Max) throw new ArgumentOutOfRangeException(nameof(c));
        var n = new[] { 3, 3, 4, 4, 5 }[c - 1]; // people
        var m = new[] { 3, 4, 4, 5, 5 }[c - 1]; // attributes, including the name
        var order = Enumerable.Range(0, Pool.Length).ToList();
        rng.Shuffle(order);
        var names = Names.ToList();
        rng.Shuffle(names);
        var attrs = new List<Attr> { new("name", "{v}", names.Take(n).ToArray()), new("position", "position {v}", Enumerable.Range(1, n).Select(i => i.ToString()).ToArray()) };
        foreach (var k in order.Take(m - 2))
        {
            var vals = Pool[k].Values.ToList();
            rng.Shuffle(vals);
            attrs.Add(Pool[k] with { Values = vals.Take(n).ToArray() });
        }
        // solution: val[a][e] = value index held by entity e; the name attribute is the identity
        var val = new int[m][];
        for (var a = 0; a < m; a++)
        {
            val[a] = Enumerable.Range(0, n).ToArray();
            if (a > 0) rng.Shuffle(val[a]);
        }

        var clues = new List<Clue>();
        var seen = new List<string>();
        while (CountSolutions(n, m, clues, 2) != 1)
        {
            if (clues.Count > 300) throw new InvalidOperationException("could not make the puzzle unique");
            var clue = RandomTrueClue(rng, n, m, val);
            var key = Sentence(attrs, clue);
            if (seen.Contains(key)) continue;
            seen.Add(key);
            clues.Add(clue);
        }
        for (var i = 0; i < clues.Count;)
        {
            var without = clues.Where((_, j) => j != i).ToList();
            if (CountSolutions(n, m, without, 2) == 1) clues = without; else i++;
        }

        var r = new Ref(attrs.ToArray(), clues.ToArray());
        var answer = AnswerJson(attrs, val);
        var swapLast = Swapped(val, m - 1);
        var swapPos = Swapped(val, 1);
        var hidden = new TaskHidden(
            [new GraderSpec("structural", "solution", JsonUtil.Element(new { expected = JsonDocument.Parse(answer).RootElement.Clone() }))],
            null, JsonUtil.Element(r));
        return new GeneratedTask(TaskPrompt.Simple(Render(r), AnswerFormat.Json), hidden,
            [new ChatMessage(Role.Assistant, $"Working...\n<answer>{answer}</answer>")],
            [Turn($"<answer>{AnswerJson(attrs, swapLast)}</answer>"), Turn($"<answer>{AnswerJson(attrs, swapPos)}</answer>"),
             Turn("The clues point to a consistent arrangement, but I would rather not commit to one.")]);
    }

    private static IReadOnlyList<ChatMessage> Turn(string text) => [new ChatMessage(Role.Assistant, text)];

    /// <summary>A copy of the solution with the values of entities 0 and 1 exchanged in one attribute.</summary>
    private static int[][] Swapped(int[][] val, int attr)
    {
        var copy = val.Select(v => (int[])v.Clone()).ToArray();
        (copy[attr][0], copy[attr][1]) = (copy[attr][1], copy[attr][0]);
        return copy;
    }

    private static string AnswerJson(IReadOnlyList<Attr> attrs, int[][] val)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            for (var e = 0; e < attrs[0].Values.Length; e++)
            {
                w.WriteStartObject(attrs[0].Values[e]);
                for (var a = 1; a < attrs.Count; a++) w.WriteString(attrs[a].Key, attrs[a].Values[val[a][e]]);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static Clue RandomTrueClue(Rng rng, int n, int m, int[][] val)
    {
        int[] Item(int a, int e) => [a, val[a][e]];
        while (true)
        {
            var e1 = rng.Next(n);
            switch (rng.Next(10))
            {
                case < 3: // same entity
                {
                    var (a, b) = TwoAttrs(rng, m);
                    return new Clue("same", Item(a, e1), Item(b, e1));
                }
                case < 5: // not the same entity
                {
                    var e2 = rng.Next(n);
                    var (a, b) = TwoAttrs(rng, m);
                    if (e1 != e2) return new Clue("not-same", Item(a, e1), Item(b, e2));
                    break;
                }
                case < 8: // left-of / next-to
                {
                    var e2 = rng.Next(n);
                    int a = PosAttr(rng, m), b = PosAttr(rng, m);
                    var (p1, p2) = (val[1][e1], val[1][e2]);
                    var d = Math.Abs(p1 - p2);
                    if (p1 < p2 && (d != 1 || rng.Chance(0.5))) return new Clue("left", Item(a, e1), Item(b, e2));
                    if (d == 1) return new Clue("next", Item(a, e1), Item(b, e2));
                    break;
                }
                default: // either-or
                {
                    var e2 = rng.Next(n);
                    var (a, b) = TwoAttrs(rng, m);
                    if (e1 == e2) break;
                    var (x, y) = rng.Chance(0.5) ? (Item(b, e1), Item(b, e2)) : (Item(b, e2), Item(b, e1));
                    return new Clue("either", Item(a, e1), x, y);
                }
            }
        }
    }

    private static (int, int) TwoAttrs(Rng rng, int m)
    {
        var a = rng.Next(m);
        var b = (a + rng.Range(1, m - 1)) % m;
        return (a, b);
    }

    /// <summary>The name attribute or a categorical one, never the position attribute itself.</summary>
    private static int PosAttr(Rng rng, int m)
    {
        var k = rng.Next(m - 1);
        return k == 0 ? 0 : k + 1;
    }

    // ---------------------------------------------------------------- generator's solver: cell-by-cell backtracking

    private static int CountSolutions(int n, int m, IReadOnlyList<Clue> clues, int limit)
    {
        var own = new int[m][]; // own[a][v] = entity holding value v of attribute a, or -1
        var used = new bool[m][];
        for (var a = 0; a < m; a++) { own[a] = new int[n]; Array.Fill(own[a], a == 0 ? -2 : -1); used[a] = new bool[n]; }
        for (var v = 0; v < n; v++) own[0][v] = v;
        // assigning a cell of attribute a can only change clues that mention a (or the position attribute, for left/next)
        var touching = Enumerable.Range(0, m).Select(a => clues.Where(cl => cl.A[0] == a || cl.B[0] == a || cl.C?[0] == a || (a == 1 && cl.Kind is "left" or "next")).ToArray()).ToArray();
        var count = 0;
        void Rec(int cell)
        {
            if (count >= limit) return;
            if (cell == (m - 1) * n) { count++; return; }
            int a = 1 + cell / n, v = cell % n;
            for (var e = 0; e < n; e++)
            {
                if (used[a][e]) continue;
                own[a][v] = e; used[a][e] = true;
                var ok = true;
                foreach (var cl in touching[a])
                    if (Eval(cl, own) == false) { ok = false; break; }
                if (ok) Rec(cell + 1);
                used[a][e] = false; own[a][v] = -1;
            }
        }
        Rec(0);
        return count;
    }

    /// <summary>Truth of a clue under a partial assignment: null while undetermined.</summary>
    private static bool? Eval(Clue c, int[][] own)
    {
        static int Ent(int[][] own, int[] it) => own[it[0]][it[1]];
        static int Pos(int[][] own, int e) => Array.IndexOf(own[1], e);
        int ea = Ent(own, c.A), eb = Ent(own, c.B);
        switch (c.Kind)
        {
            case "same": return ea < 0 || eb < 0 ? null : ea == eb;
            case "not-same": return ea < 0 || eb < 0 ? null : ea != eb;
            case "either":
                var ec = Ent(own, c.C!);
                if (ea < 0) return null;
                if (ea == eb || ea == ec) return true;
                return eb >= 0 && ec >= 0 ? false : null;
            default:
                if (ea < 0 || eb < 0) return null;
                int pa = Pos(own, ea), pb = Pos(own, eb);
                if (pa < 0 || pb < 0) return null;
                return c.Kind == "left" ? pa < pb : Math.Abs(pa - pb) == 1;
        }
    }

    // ---------------------------------------------------------------- prompt

    private static string Phrase(IReadOnlyList<Attr> attrs, int[] item)
    {
        var (a, v) = (item[0], item[1]);
        return a switch
        {
            0 => attrs[0].Values[v],
            1 => $"the person in position {attrs[1].Values[v]}",
            _ => "the person who " + attrs[a].Verb.Replace("{v}", attrs[a].Values[v]),
        };
    }

    private static string Sentence(IReadOnlyList<Attr> attrs, Clue c)
    {
        string A = Phrase(attrs, c.A), B = Phrase(attrs, c.B);
        var s = c.Kind switch
        {
            "same" => $"{A} is {B}.",
            "not-same" => $"{A} is not {B}.",
            "left" => $"{A} stands somewhere to the left of {B}.",
            "next" => $"{A} stands directly next to {B}, in an adjacent position.",
            _ => $"{A} is either {B} or {Phrase(attrs, c.C!)}.",
        };
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    private static string Render(Ref r)
    {
        var n = r.Attrs[0].Values.Length;
        var sb = new StringBuilder();
        sb.AppendLine($"{n} people stand in a row, in positions 1 (leftmost) to {n} (rightmost). Each person has exactly one value for every attribute below, and no two people share a value of the same attribute.").AppendLine();
        foreach (var a in r.Attrs) sb.AppendLine($"- {a.Key}: {string.Join(", ", a.Values)}");
        sb.AppendLine().AppendLine("Clues:");
        for (var i = 0; i < r.Clues.Length; i++) sb.AppendLine($"{i + 1}. {Sentence(r.Attrs, r.Clues[i])}");
        sb.AppendLine().AppendLine("Exactly one assignment satisfies every clue. Find it.").AppendLine()
          .Append("Give the full solution inside <answer></answer> tags as a JSON object with one key per person name, each holding an object with the keys ")
          .Append(string.Join(", ", r.Attrs.Skip(1).Select(a => $"\"{a.Key}\"")))
          .Append(". Every value is a JSON string (write positions as strings, like \"2\"). Form: <answer>{\"<name>\":{")
          .Append(string.Join(",", r.Attrs.Skip(1).Select(a => $"\"{a.Key}\":\"<value>\"")))
          .Append("},...}</answer>");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- independent validation: permutation enumeration

    public ValidationResult Validate(GeneratedTask task)
    {
        var problems = new List<string>();
        try
        {
            if (task.Prompt.AnswerFormat != AnswerFormat.Json) problems.Add("answer format must be Json");
            var r = JsonUtil.Deserialize<Ref>(task.Hidden.Reference!.Value.GetRawText());
            if (task.Prompt.User != Render(r)) problems.Add("prompt does not match the clues in the reference");
            var expectedEl = task.Hidden.Graders.Single(g => g.Type == "structural").Config.GetProperty("expected");
            var solutions = Enumerate(r, 2);
            if (solutions.Count != 1) problems.Add($"puzzle has {(solutions.Count == 0 ? "no" : "more than one")} solution");
            else
            {
                var found = AnswerJson(r.Attrs, solutions[0]);
                if (JsonDocument.Parse(found).RootElement.GetRawText() != expectedEl.GetRawText()) problems.Add("expected solution differs from the enumerated unique solution");
                if (Extract(task.ReferenceTurns.Single().Content) != expectedEl.GetRawText()) problems.Add("reference turn does not give the expected solution");
                if (task.KnownBadTurns.Count < 2) problems.Add("need at least two known-bad turns");
                foreach (var bad in task.KnownBadTurns)
                    if (Extract(bad.Last().Content) == expectedEl.GetRawText()) problems.Add("a known-bad turn contains the correct solution");
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            problems.Add($"malformed task: {ex.Message}");
        }
        return problems.Count == 0 ? ValidationResult.Valid : new ValidationResult(false, problems);
    }

    private static string? Extract(string? text)
    {
        var a = Grading.AnswerExtractor.Extract(text is null ? null : Grading.AnswerExtractor.StripReasoning(text), AnswerFormat.Json);
        return a is null ? null : JsonDocument.Parse(a).RootElement.GetRawText();
    }

    /// <summary>All solutions (up to limit) by assigning a whole permutation per attribute and checking every clue whose attributes are assigned.</summary>
    private static List<int[][]> Enumerate(Ref r, int limit)
    {
        var n = r.Attrs[0].Values.Length;
        var m = r.Attrs.Length;
        var perms = new List<int[]>();
        void Permute(int[] cur, int k)
        {
            if (k == n) { perms.Add((int[])cur.Clone()); return; }
            for (var i = k; i < n; i++)
            {
                (cur[k], cur[i]) = (cur[i], cur[k]);
                Permute(cur, k + 1);
                (cur[k], cur[i]) = (cur[i], cur[k]);
            }
        }
        Permute(Enumerable.Range(0, n).ToArray(), 0);

        // a clue can be checked once its highest attribute is assigned (left/next also need the position attribute, 1)
        int Level(Clue c) => Math.Max(c.Kind is "left" or "next" ? 1 : 0, new[] { c.A, c.B, c.C ?? c.A }.Max(it => it[0]));
        var byLevel = Enumerable.Range(0, m).Select(l => r.Clues.Where(c => Level(c) == l).ToArray()).ToArray();

        var val = new int[m][];
        val[0] = Enumerable.Range(0, n).ToArray();
        var found = new List<int[][]>();
        bool Holds(Clue c)
        {
            int E(int[] it) => Array.IndexOf(val[it[0]], it[1]);
            int ea = E(c.A), eb = E(c.B);
            return c.Kind switch
            {
                "same" => ea == eb,
                "not-same" => ea != eb,
                "left" => val[1][ea] < val[1][eb],
                "next" => Math.Abs(val[1][ea] - val[1][eb]) == 1,
                _ => ea == eb || ea == E(c.C!),
            };
        }
        void Rec(int a)
        {
            if (found.Count >= limit) return;
            if (a == m) { found.Add(val.Select(v => (int[])v.Clone()).ToArray()); return; }
            foreach (var p in perms)
            {
                val[a] = p;
                if (byLevel[a].All(Holds)) Rec(a + 1);
            }
        }
        if (!byLevel[0].All(Holds)) return found;
        Rec(1);
        return found;
    }
}
