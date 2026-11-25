using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using ModelBench.Core.Domain;
using ModelBench.Core.Generation;
using ModelBench.Core.Grading;

namespace ModelBench.Core.Families;


/// <summary>
/// Seeded exact-answer maths: rational expressions, modular arithmetic and counting.
/// Complexity scales operand sizes and expression length. Answers are canonical ("p/q" in lowest terms, or an integer).
/// </summary>
public sealed class MathematicsFamily : IBenchmarkFamily
{
    public string Id => "mathematics";
    public int Version => 2; // v2: platform-independent LF line endings
    public string Category => "mathematics";
    public string Description => "Exact rational arithmetic, modular arithmetic and combinatorics with a single exact answer.";
    public ExecutionMode Mode => ExecutionMode.SingleTurn;

    /// <summary>Generator metadata stored in TaskHidden.Reference; Validate recomputes the answer from it independently.</summary>
    private sealed record Ref(string Kind, string? Expr = null, long A = 0, long B = 0, long E = 0, long M = 0,
        int N = 0, int K = 0, int W = 0, int H = 0, int[][]? Blocked = null);

    // ---------------------------------------------------------------- generation

    public GeneratedTask Generate(Rng rng, int c)
    {
        if (c is < Complexity.Min or > Complexity.Max) throw new ArgumentOutOfRangeException(nameof(c));
        var (r, answer) = rng.Next(6) switch
        {
            0 => GenExpr(rng, c),
            1 => GenModPow(rng, c),
            2 => GenLinCong(rng, c),
            3 => GenChoose(rng, c),
            4 => GenDerange(rng, c),
            _ => GenLattice(rng, c),
        };
        var user = new StringBuilder()
            .Ln("Solve the following problem exactly.")
            .Ln()
            .Ln(Render(r))
            .Ln()
            .Append("You may show your working. Then give the final answer inside <answer></answer> tags, as a single exact value: ")
            .Append("an integer, or a fraction written as p/q in lowest terms (for example <answer>-7/12</answer>). ")
            .Append("Do not use decimals, units or any other text inside the tags.")
            .ToString();
        var hidden = new TaskHidden(
            [new GraderSpec("numeric", "value", JsonUtil.Element(new { expected = answer })),
             new GraderSpec("exact", "canonical-form", JsonUtil.Element(new { expected = answer, normalize = "none" }))],
            null, JsonUtil.Element(r));

        var reference = new ChatMessage(Role.Assistant, $"Working...\n<answer>{answer}</answer>");
        var bad = new List<IReadOnlyList<ChatMessage>>
        {
            Turn($"Working...\n<answer>{OffByOne(answer)}</answer>"),
            Turn($"Working...\nThe answer is {answer}."),
            Turn($"<think>Perhaps <answer>{answer}</answer></think>I could not finish."),
        };
        if (Rational.TryParse(answer, out var q) && !q.Den.IsOne)
            bad.Add(Turn($"<answer>{q.Num * 2}/{q.Den * 2}</answer>")); // right value, not in lowest terms
        return new GeneratedTask(TaskPrompt.Simple(user), hidden, [reference], bad);
    }

    private static IReadOnlyList<ChatMessage> Turn(string text) => [new ChatMessage(Role.Assistant, text)];

    private static string OffByOne(string answer)
    {
        Rational.TryParse(answer, out var q);
        return Rational.Of(q.Num + 1, q.Den).ToString();
    }

    private static string Render(Ref r) => r.Kind switch
    {
        "expr" => $"Evaluate the expression below. A parenthesised a/b is a fraction; × and ÷ are multiplication and division.\n\n{r.Expr}",
        "modpow" => $"Compute {r.A}^{r.E} mod {r.M}, that is the remainder when {r.A}^{r.E} is divided by {r.M}.",
        "lincong" => $"Find the smallest non-negative integer x such that {r.A}·x ≡ {r.B} (mod {r.M}).",
        "choose" => $"How many ways are there to choose {r.K} items from {r.N} distinct items, when the order of choosing does not matter?",
        "derange" => $"How many permutations of the numbers 1 to {r.N} leave no number in its original position?",
        "lattice" => $"A walker starts at (0,0) on the integer lattice and must reach ({r.W},{r.H}). Each step moves exactly one unit right (x+1) or one unit up (y+1). "
                     + $"The points {string.Join(", ", r.Blocked!.Select(p => $"({p[0]},{p[1]})"))} are blocked and must not be visited. How many different paths are there?",
        _ => throw new InvalidOperationException($"unknown kind {r.Kind}"),
    };

    private abstract record Node;
    private sealed record Leaf(Rational Value) : Node;
    private sealed record Op(char Sym, Node L, Node R) : Node;

    private static (Ref, string) GenExpr(Rng rng, int c)
    {
        while (true)
        {
            var tree = BuildExpr(rng, c + 2, c);
            if (!TryEval(tree, out var v)) continue; // division by zero: resample from the same rng
            return (new Ref("expr", Print(tree, true)), v.ToString());
        }
    }

    private static Node BuildExpr(Rng rng, int leaves, int c)
    {
        if (leaves == 1)
        {
            var max = c <= 2 ? 9 : 12;
            return new Leaf(rng.Chance(0.3) ? Rational.Of(rng.Range(1, max)) : Rational.Of(rng.Range(1, max), rng.Range(2, max)));
        }
        var left = rng.Range(1, leaves - 1);
        return new Op("+-×÷"[rng.Next(4)], BuildExpr(rng, left, c), BuildExpr(rng, leaves - left, c));
    }

    private static bool TryEval(Node n, out Rational v)
    {
        v = default;
        switch (n)
        {
            case Leaf l: v = l.Value; return true;
            case Op o:
                if (!TryEval(o.L, out var a) || !TryEval(o.R, out var b)) return false;
                if (o.Sym == '÷' && b.IsZero) return false;
                v = o.Sym switch { '+' => a + b, '-' => a - b, '×' => a * b, _ => a / b };
                return true;
            default: throw new InvalidOperationException();
        }
    }

    private static string Print(Node n, bool top) => n switch
    {
        Leaf l => l.Value.Den.IsOne ? l.Value.Num.ToString() : $"({l.Value.Num}/{l.Value.Den})",
        Op o => top ? $"{Print(o.L, false)} {o.Sym} {Print(o.R, false)}" : $"({Print(o.L, false)} {o.Sym} {Print(o.R, false)})",
        _ => throw new InvalidOperationException(),
    };

    private static int Pow10(int e) { var p = 1; for (int i = 0; i < e; i++) p *= 10; return p; }

    private static (Ref, string) GenModPow(Rng rng, int c)
    {
        var p = Pow10(c);
        long a = rng.Range(2, 10 * p), e = rng.Range(p, 100 * p), m = rng.Range(p + 1, 10 * p);
        return (new Ref("modpow", A: a, E: e, M: m), BigInteger.ModPow(a, e, m).ToString());
    }

    private static (Ref, string) GenLinCong(Rng rng, int c)
    {
        var p = Pow10(c);
        long m = rng.Range(p + 1, 10 * p), a;
        do a = rng.Range(2, (int)m - 1); while (BigInteger.GreatestCommonDivisor(a, m) != 1);
        long b = rng.Range(0, (int)m - 1);
        // x = b * a^-1 mod m via extended Euclid
        var (_, s, _) = ExtGcd(a, m);
        var x = (((BigInteger)b * s) % m + m) % m;
        return (new Ref("lincong", A: a, B: b, M: m), x.ToString());
    }

    private static (BigInteger g, BigInteger x, BigInteger y) ExtGcd(BigInteger a, BigInteger b)
    {
        if (b.IsZero) return (a, 1, 0);
        var (g, x, y) = ExtGcd(b, a % b);
        return (g, y, x - a / b * y);
    }

    private static (Ref, string) GenChoose(Rng rng, int c)
    {
        int n = rng.Range(5 + 3 * c, 8 + 5 * c), k = rng.Range(2, n - 2);
        BigInteger r = 1;
        for (int i = 1; i <= k; i++) r = r * (n - k + i) / i; // exact at every step
        return (new Ref("choose", N: n, K: k), r.ToString());
    }

    private static (Ref, string) GenDerange(Rng rng, int c)
    {
        int n = 3 + c;
        BigInteger d0 = 1, d1 = 0; // D(0), D(1)
        for (int i = 2; i <= n; i++) (d0, d1) = (d1, (i - 1) * (d0 + d1));
        return (new Ref("derange", N: n), d1.ToString());
    }

    private static (Ref, string) GenLattice(Rng rng, int c)
    {
        while (true)
        {
            int w = rng.Range(c + 2, c + 3), h = rng.Range(c + 2, c + 3);
            var blocked = new List<int[]>();
            while (blocked.Count < c)
            {
                int x = rng.Range(0, w), y = rng.Range(0, h);
                if ((x == 0 && y == 0) || (x == w && y == h) || blocked.Any(p => p[0] == x && p[1] == y)) continue;
                blocked.Add([x, y]);
            }
            blocked.Sort((p, q) => p[0] != q[0] ? p[0].CompareTo(q[0]) : p[1].CompareTo(q[1]));
            var dp = new BigInteger[w + 1, h + 1];
            for (int x = 0; x <= w; x++)
                for (int y = 0; y <= h; y++)
                {
                    if (blocked.Any(p => p[0] == x && p[1] == y)) continue;
                    dp[x, y] = x == 0 && y == 0 ? 1 : (x > 0 ? dp[x - 1, y] : 0) + (y > 0 ? dp[x, y - 1] : 0);
                }
            if (dp[w, h].IsZero) continue;
            return (new Ref("lattice", W: w, H: h, Blocked: blocked.ToArray()), dp[w, h].ToString());
        }
    }

    // ---------------------------------------------------------------- independent validation

    public ValidationResult Validate(GeneratedTask task)
    {
        var problems = new List<string>();
        try
        {
            if (task.Prompt.AnswerFormat != AnswerFormat.Tagged) problems.Add("answer format must be Tagged");
            if (!task.Prompt.User.Contains("p/q in lowest terms")) problems.Add("prompt does not state the fraction format");
            var r = task.Hidden.Reference is { } re ? JsonUtil.Deserialize<Ref>(re.GetRawText()) : null;
            if (r is null) return ValidationResult.Fail("missing reference metadata");
            if (!task.Prompt.User.Contains(Render(r))) problems.Add("prompt does not contain the problem described by the reference");

            string? numeric = null, exact = null;
            foreach (var g in task.Hidden.Graders)
            {
                var e = g.Config.GetProperty("expected").GetString();
                if (g.Type == "numeric") numeric = e; else if (g.Type == "exact") exact = e;
            }
            var independent = Independent(r);
            if (numeric != independent) problems.Add($"numeric expected {numeric} != independent {independent}");
            if (exact != independent) problems.Add($"exact expected {exact} != independent {independent}");
            if (!Rational.TryParse(independent, out var canon) || canon.ToString() != independent) problems.Add("answer is not canonical");

            var refAnswer = ExtractFrom(task.ReferenceTurns.Single().Content);
            if (refAnswer != independent) problems.Add($"reference turn answers {refAnswer}, expected {independent}");
            if (task.KnownBadTurns.Count < 2) problems.Add("need at least two known-bad turns");
            foreach (var bad in task.KnownBadTurns)
                if (ExtractFrom(bad.Last().Content) == independent) problems.Add("a known-bad turn contains the correct answer");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            problems.Add($"malformed task: {ex.Message}");
        }
        return problems.Count == 0 ? ValidationResult.Valid : new ValidationResult(false, problems);
    }

    private static string? ExtractFrom(string? text)
        => AnswerExtractor.Extract(text is null ? null : AnswerExtractor.StripReasoning(text), AnswerFormat.Tagged);

    /// <summary>Recomputes the answer by methods different from the generator's.</summary>
    private static string Independent(Ref r) => r.Kind switch
    {
        "expr" => EvalRpn(r.Expr!),
        "modpow" => ModPowManual(r.A, r.E, r.M).ToString(),
        "lincong" => LinCongDirect(r.A, r.B, r.M).ToString(),
        "choose" => ChooseCheck(r.N, r.K).ToString(),
        "derange" => DerangePermutations(r.N).ToString(),
        "lattice" => LatticeDfs(r).ToString(),
        _ => throw new InvalidOperationException($"unknown kind {r.Kind}"),
    };

    /// <summary>Shunting-yard to RPN, then evaluation on unreduced (num, den) pairs, reducing only at the end.</summary>
    private static string EvalRpn(string expr)
    {
        var output = new List<string>();
        var ops = new Stack<char>();
        static int Prec(char o) => o is '+' or '-' ? 1 : 2;
        foreach (Match m in Regex.Matches(expr, @"\(\d+/\d+\)|\d+|[-+×÷()]"))
        {
            var t = m.Value;
            if (t.Length > 1 || char.IsAsciiDigit(t[0])) { output.Add(t); continue; }
            var ch = t[0];
            if (ch == '(') ops.Push(ch);
            else if (ch == ')')
            {
                while (ops.Peek() != '(') output.Add(ops.Pop().ToString());
                ops.Pop();
            }
            else
            {
                while (ops.Count > 0 && ops.Peek() != '(' && Prec(ops.Peek()) >= Prec(ch)) output.Add(ops.Pop().ToString());
                ops.Push(ch);
            }
        }
        while (ops.Count > 0) output.Add(ops.Pop().ToString());

        var st = new Stack<(BigInteger n, BigInteger d)>();
        foreach (var t in output)
        {
            if (t.Length > 1 || char.IsAsciiDigit(t[0]))
            {
                var parts = t.Trim('(', ')').Split('/');
                st.Push((BigInteger.Parse(parts[0]), parts.Length == 2 ? BigInteger.Parse(parts[1]) : 1));
                continue;
            }
            var (bn, bd) = st.Pop();
            var (an, ad) = st.Pop();
            st.Push(t[0] switch
            {
                '+' => (an * bd + bn * ad, ad * bd),
                '-' => (an * bd - bn * ad, ad * bd),
                '×' => (an * bn, ad * bd),
                '÷' => bn.IsZero ? throw new InvalidOperationException("division by zero") : (an * bd, ad * bn),
                _ => throw new InvalidOperationException($"bad operator {t}"),
            });
        }
        var (n, d) = st.Single();
        if (d.Sign < 0) (n, d) = (-n, -d);
        var g = BigInteger.GreatestCommonDivisor(n, d);
        (n, d) = (n / g, d / g);
        return d.IsOne ? n.ToString() : $"{n}/{d}";
    }

    private static BigInteger ModPowManual(long a, long e, long m)
    {
        BigInteger result = 1, b = a % m;
        for (; e > 0; e >>= 1)
        {
            if ((e & 1) == 1) result = result * b % m;
            b = b * b % m;
        }
        return result;
    }

    private static BigInteger LinCongDirect(long a, long b, long m)
    {
        if (BigInteger.GreatestCommonDivisor(a, m) != 1) throw new InvalidOperationException("a and m not coprime");
        for (long x = 0; x < m; x++) if (a * x % m == b % m) return x; // smallest solution by search
        throw new InvalidOperationException("no solution");
    }

    private static BigInteger ChooseCheck(int n, int k)
    {
        if (n <= 16)
        {
            int count = 0;
            for (int mask = 0; mask < 1 << n; mask++) if (BitOperations.PopCount((uint)mask) == k) count++;
            return count;
        }
        var row = new BigInteger[n + 1];
        row[0] = 1;
        for (int i = 1; i <= n; i++)
            for (int j = i; j >= 1; j--) row[j] += row[j - 1]; // Pascal's triangle
        return row[k];
    }

    private static BigInteger DerangePermutations(int n)
    {
        var perm = Enumerable.Range(0, n).ToArray();
        BigInteger count = 0;
        void Rec(int i)
        {
            if (i == n) { if (Enumerable.Range(0, n).All(j => perm[j] != j)) count++; return; }
            for (int j = i; j < n; j++)
            {
                (perm[i], perm[j]) = (perm[j], perm[i]);
                Rec(i + 1);
                (perm[i], perm[j]) = (perm[j], perm[i]);
            }
        }
        Rec(0);
        return count;
    }

    private static BigInteger LatticeDfs(Ref r)
    {
        BigInteger Walk(int x, int y)
        {
            if (x > r.W || y > r.H || r.Blocked!.Any(p => p[0] == x && p[1] == y)) return 0;
            if (x == r.W && y == r.H) return 1;
            return Walk(x + 1, y) + Walk(x, y + 1);
        }
        return Walk(0, 0);
    }
}
