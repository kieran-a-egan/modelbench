using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelBench.Core.Domain;
using ModelBench.Core.Generation;
using static ModelBench.Core.Families.CodeKit;

namespace ModelBench.Core.Families;

/// <summary>
/// Fix the seeded bugs in a Python function. A correct template carries marked mutation sites <c>{{kind:original ~> mutant}}</c>;
/// 1..3 sites (by complexity) are applied textually. Each function has a C# port with a switch per site (a mirror of the
/// Python logic) so the host can prove every mutation changes behaviour without ever executing Python.
/// </summary>
public sealed partial class DebuggingFamily : IBenchmarkFamily
{
    public string Id => "debugging";
    public int Version => 2; // v2: platform-independent LF line endings
    public string Category => "debugging";
    public string Description => "Fix a Python function that carries 1-3 seeded bugs (flipped comparison, off-by-one, wrong variable, removed base case, swapped arguments).";
    public ExecutionMode Mode => ExecutionMode.SingleTurn;

    private sealed record Fn(
        string Name, string Desc, string Template, Func<Rng, int, object[]> Gen, Func<JsonElement[], object> Solve,
        Func<JsonElement[], Func<int, bool>, object> Port, object[][] Edge);

    private sealed record Ref(string Fn, int[] Sites, JsonElement[] Failing);

    [GeneratedRegex(@"\{\{(\w+):(.*?)~>(.*?)\}\}")] private static partial Regex Site();

    private static int[] Ints(JsonElement e) => e.Deserialize<int[]>()!;
    private static int Int(JsonElement e) => e.GetInt32();
    private static string Str(JsonElement e) => e.GetString()!;
    private static string Word(Rng rng, int maxLen) => new(Enumerable.Range(0, rng.Range(0, maxLen)).Select(_ => "abc"[rng.Next(3)]).ToArray());

    private static string Apply(string template, IReadOnlyCollection<int> on)
    {
        var i = -1;
        return Site().Replace(template, m => (on.Contains(++i) ? m.Groups[3] : m.Groups[2]).Value.Trim());
    }

    private static readonly IReadOnlyList<Fn> Fns =
    [
        new("max_subarray_sum", "return the maximum sum of a non-empty contiguous sub-list of nums (nums is never empty).",
            """
            def max_subarray_sum(nums):
                best = nums[0]
                cur = nums[0]
                for i in range({{bound:1 ~> 0}}, len(nums)):
                    cur = max(nums[i], cur + nums[i])
                    if cur {{cmp:> ~> <}} best:
                        best = {{var:cur ~> nums[i]}}
                return best
            """,
            (rng, c) => [Enumerable.Range(0, rng.Range(1, 3 + 2 * c)).Select(_ => rng.Range(-9, 9)).ToArray()],
            a => { var n = Ints(a[0]); return Enumerable.Range(0, n.Length).Max(i => Enumerable.Range(i, n.Length - i).Max(j => n[i..(j + 1)].Sum())); },
            (a, m) =>
            {
                var n = Ints(a[0]); int best = n[0], cur = n[0];
                for (int i = m(0) ? 0 : 1; i < n.Length; i++)
                {
                    cur = Math.Max(n[i], cur + n[i]);
                    if (m(1) ? cur < best : cur > best) best = m(2) ? n[i] : cur;
                }
                return best;
            },
            [[new[] { -3 }], [new[] { -2, -1, -3 }], [new[] { 1, 2, 3 }], [new[] { 5, -9, 6 }], [new[] { 4, -1, 2, 1 }]]),

        new("is_palindrome", "return True when the string s reads the same forwards and backwards (the empty string is a palindrome), otherwise False.",
            """
            def is_palindrome(s):
                i, j = 0, len(s) - {{bound:1 ~> 0}}
                while i {{cmp:< ~> >}} j:
                    if s[i] != s[{{var:j ~> i}}]:
                        return False
                    i += 1
                    j -= 1
                return True
            """,
            (rng, c) =>
            {
                var half = Word(rng, 1 + c);
                var s = half + (rng.Chance(0.5) ? "" : "c") + new string(half.Reverse().ToArray());
                if (rng.Chance(0.5) && s.Length > 0) s = s.Remove(0, 1).Insert(0, "abc"[rng.Next(3)].ToString());
                return [s];
            },
            a => { var s = Str(a[0]); return s == new string(s.Reverse().ToArray()); },
            (a, m) =>
            {
                var s = Str(a[0]); int i = 0, j = s.Length - (m(0) ? 0 : 1);
                while (m(1) ? i > j : i < j)
                {
                    if (s[i] != s[m(2) ? i : j]) return false;
                    i++; j--;
                }
                return true;
            },
            [[""], ["a"], ["ab"], ["aba"], ["abca"], ["abba"]]),

        new("fib", "return the n-th Fibonacci number, with fib(0) = 0 and fib(1) = 1 (n >= 0).",
            """
            def fib(n):
                {{base:if n == 0: return 0 ~> pass}}
                if n {{cmp:== ~> !=}} 1:
                    return 1
                return fib(n - 1) + fib(n - {{bound:2 ~> 1}})
            """,
            (rng, c) => [rng.Range(0, 12 + c)],
            a => { long x = 0, y = 1; for (int i = 0; i < Int(a[0]); i++) (x, y) = (y, x + y); return x; },
            (a, m) =>
            {
                long F(int n, int d)
                {
                    if (d > 300) throw new InvalidOperationException("recursion");
                    if (!m(0) && n == 0) return 0;
                    if (m(1) ? n != 1 : n == 1) return 1;
                    return F(n - 1, d + 1) + F(n - (m(2) ? 1 : 2), d + 1);
                }
                return F(Int(a[0]), 0);
            },
            [[0], [1], [2], [3], [7]]),

        new("gcd", "return the greatest common divisor of the non-negative integers a and b; gcd(a, 0) is a.",
            """
            def gcd(a, b):
                if b {{cmp:== ~> !=}} 0:
                    return {{var:a ~> b}}
                return gcd({{args:b, a % b ~> a % b, b}})
            """,
            (rng, c) => [rng.Range(0, 20 + 10 * c), rng.Range(0, 20 + 10 * c)],
            a => { int x = Int(a[0]), y = Int(a[1]); for (int d = Math.Max(x, y); d >= 1; d--) if (x % d == 0 && y % d == 0) return d; return 0; },
            (a, m) =>
            {
                int G(int x, int y, int d)
                {
                    if (d > 300) throw new InvalidOperationException("recursion");
                    if (m(0) ? y != 0 : y == 0) return m(1) ? y : x;
                    return m(2) ? G(x % y, y, d + 1) : G(y, x % y, d + 1);
                }
                return G(Int(a[0]), Int(a[1]), 0);
            },
            [[12, 8], [7, 0], [0, 5], [9, 28], [48, 18]]),

        new("count_pairs", "return how many pairs of positions i < j satisfy nums[i] + nums[j] == target.",
            """
            def count_pairs(nums, target):
                count = 0
                for i in range(len(nums)):
                    for j in range({{bound:i + 1 ~> i}}, len(nums)):
                        if nums[i] + nums[{{var:j ~> i}}] {{cmp:== ~> >=}} target:
                            count += 1
                return count
            """,
            (rng, c) => [Enumerable.Range(0, rng.Range(1, 4 + c)).Select(_ => rng.Range(0, 5)).ToArray(), rng.Range(0, 10)],
            a => { var n = Ints(a[0]); return (from i in Enumerable.Range(0, n.Length) from j in Enumerable.Range(i + 1, n.Length - i - 1) where n[i] + n[j] == Int(a[1]) select 1).Count(); },
            (a, m) =>
            {
                var n = Ints(a[0]); var t = Int(a[1]); var count = 0;
                for (int i = 0; i < n.Length; i++)
                    for (int j = m(0) ? i : i + 1; j < n.Length; j++)
                        if (m(2) ? n[i] + n[m(1) ? i : j] >= t : n[i] + n[m(1) ? i : j] == t) count++;
                return count;
            },
            [[new[] { 1, 2, 3, 4 }, 5], [new[] { 2, 2, 2 }, 4], [new[] { 3 }, 6], [new[] { 1, 1 }, 2], [new[] { 0, 5, 5 }, 10]]),

        new("binary_search", "return the index of target in the ascending list of distinct integers nums, or -1 when it is absent.",
            """
            def binary_search(nums, target):
                lo, hi = 0, {{bound:len(nums) - 1 ~> len(nums)}}
                while lo {{cmp:<= ~> <}} hi:
                    mid = (lo + hi) // 2
                    if nums[mid] == target:
                        return mid
                    if nums[mid] < target:
                        lo = mid + 1
                    else:
                        hi = {{var:mid - 1 ~> lo - 1}}
                return -1
            """,
            (rng, c) =>
            {
                var vals = Enumerable.Range(0, 30).ToList(); rng.Shuffle(vals);
                var n = vals.Take(rng.Range(0, 4 + 2 * c)).Order().ToArray();
                return [n, n.Length > 0 && rng.Chance(0.6) ? n[rng.Next(n.Length)] : rng.Range(0, 30)];
            },
            a => Array.IndexOf(Ints(a[0]), Int(a[1])),
            (a, m) =>
            {
                var n = Ints(a[0]); var t = Int(a[1]); int lo = 0, hi = m(0) ? n.Length : n.Length - 1;
                while (m(1) ? lo < hi : lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    if (n[mid] == t) return mid;
                    if (n[mid] < t) lo = mid + 1; else hi = m(2) ? lo - 1 : mid - 1;
                }
                return -1;
            },
            [[new[] { 1, 3, 5, 7 }, 7], [new[] { 1, 3, 5, 7 }, 1], [new[] { 4 }, 4], [new[] { 4 }, 9], [new int[0], 3], [new[] { 2, 4, 6 }, 5]]),
    ];

    // ---------------------------------------------------------------- behaviour of the mirrors

    private static string? Eval(Fn f, JsonElement[] a, IReadOnlyCollection<int> on)
    {
        try { return J(f.Port(a, on.Contains)); }
        catch (Exception) { return null; } // the Python mutant raises here too
    }

    private static bool Differs(Fn f, IEnumerable<object[]> cases, IReadOnlyCollection<int> on)
        => cases.Any(c => Eval(f, Elems(c), on) != J(f.Solve(Elems(c))));

    /// <summary>Every non-empty subset of the applied mutations must fail at least one case: each bug matters and no partial fix passes.</summary>
    private static bool EverySubsetFails(Fn f, IEnumerable<object[]> cases, int[] sites)
    {
        var all = cases.ToList();
        for (var mask = 1; mask < 1 << sites.Length; mask++)
            if (!Differs(f, all, sites.Where((_, i) => (mask >> i & 1) == 1).ToList())) return false;
        return true;
    }

    // ---------------------------------------------------------------- generation

    private static object Case(Fn f, object[] args) => new { args, expected = f.Solve(Elems(args)) };

    public GeneratedTask Generate(Rng rng, int c)
    {
        if (c is < Complexity.Min or > Complexity.Max) throw new ArgumentOutOfRangeException(nameof(c));
        var fn = rng.Pick(Fns);
        var siteCount = Site().Matches(fn.Template).Count;
        var bugs = (c + 1) / 2;
        var nRandom = 5 + 6 * (c - 1);

        var used = fn.Edge.Select(J).ToList();
        var pool = new List<object[]>();
        for (var attempts = 0; pool.Count < nRandom + 10; attempts++)
        {
            if (attempts > 5000) throw new InvalidOperationException($"{fn.Name}: input domain too small");
            var args = fn.Gen(rng, c);
            if (!used.Contains(J(args))) pool.Add(args); // duplicates are fine: small input domains (fib) cannot supply 29 distinct cases
        }

        for (var attempt = 0; attempt < 500; attempt++)
        {
            var order = Enumerable.Range(0, siteCount).ToList(); rng.Shuffle(order);
            var sites = order.Take(bugs).Order().ToArray();
            var failing = pool.FirstOrDefault(p => Differs(fn, [p], sites));
            if (failing is null) continue;
            var random = pool.Where(p => J(p) != J(failing)).Take(nRandom).ToList();
            if (random.Count < nRandom) continue;
            if (!EverySubsetFails(fn, fn.Edge.Concat(random), sites)) continue;

            var buggy = Apply(fn.Template, sites);
            var good = Apply(fn.Template, []);
            var user = new StringBuilder()
                .Ln($"The following Python 3 function is meant to {fn.Desc}")
                .Ln("It contains bugs. Fix them and reply with the complete corrected function in exactly one fenced code block.")
                .Ln().Ln("```python").Ln(buggy).Ln("```").Ln()
                .Append($"For example, {Call(fn.Name, failing)} should return {J(fn.Solve(Elems(failing)))}, but the code above does not.")
                .ToString();
            var graders = new List<GraderSpec>
            {
                new("compile", "compile", JsonUtil.Element(new { language = "python", entryPoint = fn.Name })),
                new("unit-tests", "unit-tests", JsonUtil.Element(new { language = "python", entryPoint = fn.Name, cases = fn.Edge.Select(a => Case(fn, a)) }), 3),
                new("differential", "differential", JsonUtil.Element(new { language = "python", entryPoint = fn.Name, cases = random.Select(a => Case(fn, a)) }), 3),
            };
            var bad = new List<IReadOnlyList<ChatMessage>> { Answer("python", buggy), Answer("python", Broken(good)) };
            if (sites.Length > 1) bad.Add(Answer("python", Apply(fn.Template, sites[1..])));
            return new GeneratedTask(new TaskPrompt(null, user, AnswerFormat.CodeBlock, []),
                new TaskHidden(graders, null, JsonUtil.Element(new { fn = fn.Name, sites, failing })),
                Answer("python", good), bad);
        }
        throw new InvalidOperationException($"{fn.Name}: no mutation set changes behaviour on the generated cases");
    }

    // ---------------------------------------------------------------- independent validation

    public ValidationResult Validate(GeneratedTask task)
    {
        var problems = new List<string>();
        if (task.Hidden.Reference is not { } reference) return ValidationResult.Fail("missing reference metadata");
        var r = JsonUtil.Deserialize<Ref>(reference.GetRawText());
        var fn = Fns.FirstOrDefault(f => f.Name == r.Fn);
        if (fn is null) return ValidationResult.Fail($"unknown function '{r.Fn}'");

        var buggy = Apply(fn.Template, r.Sites);
        if (buggy == Apply(fn.Template, [])) problems.Add("mutated code equals the original");
        if (!task.Prompt.User.Contains(buggy)) problems.Add("prompt does not contain the mutated code");

        var cases = task.Hidden.Graders.Where(g => g.Type is "unit-tests" or "differential")
            .SelectMany(g => g.Config.GetProperty("cases").EnumerateArray()).ToList();
        var hidden = new List<object[]>();
        foreach (var c in cases)
        {
            var args = c.GetProperty("args").EnumerateArray().ToArray();
            hidden.Add(args.Select(e => (object)e).ToArray());
            // The mirror of the ORIGINAL logic must agree with the stored expected value.
            if (Eval(fn, args, []) != c.GetProperty("expected").GetRawText()) problems.Add($"mirror of the original disagrees with stored expected on {c.GetProperty("args").GetRawText()}");
        }
        if (!EverySubsetFails(fn, hidden, r.Sites)) problems.Add("some mutation (or partial fix) never changes behaviour on a hidden case");

        var example = r.Failing.Select(e => (object)e).ToArray();
        if (hidden.Any(h => J(h) == J(example))) problems.Add("the failing example is also a hidden case");
        if (!Differs(fn, [example], r.Sites)) problems.Add("the failing example does not fail the mutated logic");
        var want = Eval(fn, r.Failing, []);
        if (want is null || !task.Prompt.User.Contains($"{Call(fn.Name, example)} should return {want}")) problems.Add("failing example text is not in the prompt");
        return problems.Count == 0 ? ValidationResult.Valid : new ValidationResult(false, problems);
    }
}
