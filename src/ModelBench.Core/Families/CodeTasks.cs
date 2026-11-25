using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelBench.Core.Generation;

namespace ModelBench.Core.Families;

/// <summary>A large-input case that separates the right complexity class from a quadratic one.</summary>
internal sealed record PerfCase(Func<Rng, object[]> Gen, int BudgetMs, string Quadratic);

/// <summary>
/// One function task of the code-generation family. Solve is the host reference solver that produces expected values;
/// Check is a second, algorithmically different implementation used only by Validate.
/// Entry is the Python function name or the C# "Solution.Method".
/// </summary>
internal sealed record CodeTask(
    string Id, string Lang, string Entry, string Sig, string Desc,
    Func<Rng, int, object[]> Gen, Func<JsonElement[], object> Solve, Func<JsonElement[], object> Check,
    object[][] Examples, object[][] Edge, string Ref, string Wrong,
    PerfCase? Perf = null, string? Forbid = null)
{
    public string Method => Entry[(Entry.LastIndexOf('.') + 1)..];
}

internal static class CodeTasks
{
    private static int[] Ints(JsonElement e) => e.Deserialize<int[]>()!;
    private static string Str(JsonElement e) => e.GetString()!;
    private static int Int(JsonElement e) => e.GetInt32();

    private static string Word(Rng rng, int maxLen, string alphabet)
        => new(Enumerable.Range(0, rng.Range(0, maxLen)).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());

    private static int[] RandInts(Rng rng, int n, int lo, int hi) => Enumerable.Range(0, n).Select(_ => rng.Range(lo, hi)).ToArray();

    /// <summary>n distinct values, an index pair and its target such that exactly one pair of indices sums to the target.</summary>
    private static object[] TwoSumArgs(Rng rng, int n, int maxValue, bool late)
    {
        while (true)
        {
            var seen = new HashSet<int>();
            var vals = new List<int>();
            while (vals.Count < n) { var v = rng.Range(0, maxValue); if (seen.Add(v)) vals.Add(v); }
            var i = late ? rng.Range(n * 3 / 4, n - 2) : rng.Next(n - 1);
            var j = late ? n - 1 : rng.Range(i + 1, n - 1);
            var target = vals[i] + vals[j];
            if (vals.Count(x => seen.Contains(target - x) && x < target - x) == 1) return [vals.ToArray(), target];
        }
    }

    private static int[][] RandIntervals(Rng rng, int n, int span, int maxLen)
        => Enumerable.Range(0, n).Select(_ => { var a = rng.Range(0, span); return new[] { a, a + rng.Range(1, maxLen) }; }).ToArray();

    public static readonly IReadOnlyList<CodeTask> All =
    [
        new("run-length-encode", "csharp", "Solution.RunLengthEncode", "public static string RunLengthEncode(string s)",
            "Run-length encode a string of lowercase letters: each maximal run of one letter becomes the letter followed by the decimal run length. The empty string encodes to the empty string.",
            (rng, c) => [Word(rng, 6 + 4 * c, "abc")],
            a => { var s = Str(a[0]); var sb = new StringBuilder(); for (int i = 0; i < s.Length;) { int j = i; while (j < s.Length && s[j] == s[i]) j++; sb.Append(s[i]).Append(j - i); i = j; } return sb.ToString(); },
            a => Regex.Replace(Str(a[0]), "(.)\\1*", m => m.Value[0] + m.Length.ToString()),
            [["aaabcc"], ["z"], ["abab"]], [[""], ["aaaaaaaaaaaa"], ["bbbaaaaaaaaaab"], ["abc"]],
            """
            public static class Solution
            {
                public static string RunLengthEncode(string s)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < s.Length;)
                    {
                        int j = i;
                        while (j < s.Length && s[j] == s[i]) j++;
                        sb.Append(s[i]).Append(j - i);
                        i = j;
                    }
                    return sb.ToString();
                }
            }
            """,
            """
            public static class Solution
            {
                public static string RunLengthEncode(string s)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < s.Length;)
                    {
                        int j = i;
                        while (j < s.Length && s[j] == s[i]) j++;
                        sb.Append(s[i]).Append(j - i - 1);
                        i = j;
                    }
                    return sb.ToString();
                }
            }
            """),

        new("longest-common-prefix", "csharp", "Solution.LongestCommonPrefix", "public static string LongestCommonPrefix(string[] strs)",
            "Return the longest string that is a prefix of every string in the array. The array is never empty; the result may be the empty string.",
            (rng, c) =>
            {
                var prefix = Word(rng, 5, "abc");
                return [Enumerable.Range(0, rng.Range(2, 3 + c)).Select(_ => prefix + Word(rng, 4, "abc")).ToArray()];
            },
            a => { var s = a[0].Deserialize<string[]>()!; int i = 0; while (i < s.Min(x => x.Length) && s.All(x => x[i] == s[0][i])) i++; return s[0][..i]; },
            a => { var s = a[0].Deserialize<string[]>()!; var p = s[0]; foreach (var x in s) while (!x.StartsWith(p, StringComparison.Ordinal)) p = p[..^1]; return p; },
            [[new[] { "flower", "flow", "flight" }], [new[] { "dog", "racecar" }], [new[] { "same", "same" }]],
            [[new[] { "ab", "abc", "abd" }], [new[] { "", "abc" }], [new[] { "a" }], [new[] { "abc", "abcd", "abx", "ab" }]],
            """
            public static class Solution
            {
                public static string LongestCommonPrefix(string[] strs)
                {
                    var p = strs[0];
                    foreach (var s in strs)
                        while (!s.StartsWith(p, StringComparison.Ordinal)) p = p[..^1];
                    return p;
                }
            }
            """,
            """
            public static class Solution
            {
                public static string LongestCommonPrefix(string[] strs)
                {
                    var p = strs[0];
                    foreach (var s in strs.Take(2))
                        while (!s.StartsWith(p, StringComparison.Ordinal)) p = p[..^1];
                    return p;
                }
            }
            """),

        new("integer-to-roman", "csharp", "Solution.ToRoman", "public static string ToRoman(int n)",
            "Convert an integer from 1 to 3999 to its Roman numeral using the standard subtractive forms (IV, IX, XL, XC, CD, CM).",
            (rng, c) => [rng.Range(1, 3999)],
            a =>
            {
                int[] v = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
                string[] r = ["M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"];
                var n = Int(a[0]); var sb = new StringBuilder();
                for (int i = 0; i < v.Length; i++) while (n >= v[i]) { sb.Append(r[i]); n -= v[i]; }
                return sb.ToString();
            },
            a =>
            {
                string[] th = ["", "M", "MM", "MMM"], h = ["", "C", "CC", "CCC", "CD", "D", "DC", "DCC", "DCCC", "CM"],
                    t = ["", "X", "XX", "XXX", "XL", "L", "LX", "LXX", "LXXX", "XC"], o = ["", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX"];
                var n = Int(a[0]);
                return th[n / 1000] + h[n / 100 % 10] + t[n / 10 % 10] + o[n % 10];
            },
            [[3], [58], [1994]], [[1], [4], [9], [3999], [444]],
            """
            public static class Solution
            {
                public static string ToRoman(int n)
                {
                    int[] v = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
                    string[] r = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
                    var sb = new StringBuilder();
                    for (int i = 0; i < v.Length; i++)
                        while (n >= v[i]) { sb.Append(r[i]); n -= v[i]; }
                    return sb.ToString();
                }
            }
            """,
            """
            public static class Solution
            {
                public static string ToRoman(int n)
                {
                    int[] v = { 1000, 500, 100, 50, 10, 5, 1 };
                    string[] r = { "M", "D", "C", "L", "X", "V", "I" };
                    var sb = new StringBuilder();
                    for (int i = 0; i < v.Length; i++)
                        while (n >= v[i]) { sb.Append(r[i]); n -= v[i]; }
                    return sb.ToString();
                }
            }
            """),

        new("edit-distance", "csharp", "Solution.EditDistance", "public static int EditDistance(string a, string b)",
            "Return the Levenshtein distance between two strings: the minimum number of single-character insertions, deletions and substitutions that turn a into b.",
            (rng, c) => [Word(rng, 4 + 2 * c, "abc"), Word(rng, 4 + 2 * c, "abc")],
            a =>
            {
                string s = Str(a[0]), t = Str(a[1]);
                var d = new int[s.Length + 1, t.Length + 1];
                for (int i = 0; i <= s.Length; i++) d[i, 0] = i;
                for (int j = 0; j <= t.Length; j++) d[0, j] = j;
                for (int i = 1; i <= s.Length; i++)
                    for (int j = 1; j <= t.Length; j++)
                        d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (s[i - 1] == t[j - 1] ? 0 : 1));
                return d[s.Length, t.Length];
            },
            a =>
            {
                string s = Str(a[0]), t = Str(a[1]);
                var memo = new Dictionary<(int, int), int>();
                int F(int i, int j)
                {
                    if (i == s.Length) return t.Length - j;
                    if (j == t.Length) return s.Length - i;
                    if (memo.TryGetValue((i, j), out var m)) return m;
                    return memo[(i, j)] = s[i] == t[j] ? F(i + 1, j + 1) : 1 + Math.Min(F(i + 1, j + 1), Math.Min(F(i + 1, j), F(i, j + 1)));
                }
                return F(0, 0);
            },
            [["kitten", "sitting"], ["abc", "abc"], ["", "abc"]], [["", ""], ["flaw", "lawn"], ["intention", "execution"], ["a", "b"], ["abc", ""]],
            """
            public static class Solution
            {
                public static int EditDistance(string a, string b)
                {
                    var prev = new int[b.Length + 1];
                    for (int j = 0; j <= b.Length; j++) prev[j] = j;
                    for (int i = 1; i <= a.Length; i++)
                    {
                        var cur = new int[b.Length + 1];
                        cur[0] = i;
                        for (int j = 1; j <= b.Length; j++)
                            cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                        prev = cur;
                    }
                    return prev[b.Length];
                }
            }
            """,
            """
            public static class Solution
            {
                public static int EditDistance(string a, string b)
                {
                    var prev = new int[b.Length + 1];
                    for (int j = 0; j <= b.Length; j++) prev[j] = j;
                    for (int i = 1; i <= a.Length; i++)
                    {
                        var cur = new int[b.Length + 1];
                        cur[0] = i;
                        for (int j = 1; j <= b.Length; j++)
                            cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 2));
                        prev = cur;
                    }
                    return prev[b.Length];
                }
            }
            """),

        new("balanced-brackets", "csharp", "Solution.IsBalanced", "public static bool IsBalanced(string s)",
            "Return true when the string of the bracket characters ()[]{} is balanced: every bracket is closed by the same kind of bracket in the correct order. The empty string is balanced.",
            (rng, c) =>
            {
                var n = rng.Range(0, 3 + c);
                var sb = new StringBuilder(); var st = new Stack<char>();
                for (int i = 0; i < n; i++)
                {
                    var k = rng.Next(3);
                    sb.Append("([{"[k]); st.Push(")]}"[k]);
                    if (rng.Chance(0.4)) sb.Append(st.Pop());
                }
                while (st.Count > 0) sb.Append(st.Pop());
                if (rng.Chance(0.5) && sb.Length > 0) sb[rng.Next(sb.Length)] = "()[]{}"[rng.Next(6)];
                return [sb.ToString()];
            },
            a =>
            {
                var st = new Stack<char>();
                foreach (var ch in Str(a[0]))
                {
                    if ("([{".Contains(ch)) { st.Push(ch); continue; }
                    if (st.Count == 0 || "([{".IndexOf(st.Pop()) != ")]}".IndexOf(ch)) return false;
                }
                return st.Count == 0;
            },
            a => { var s = Str(a[0]); while (true) { var t = s.Replace("()", "").Replace("[]", "").Replace("{}", ""); if (t == s) break; s = t; } return s.Length == 0; },
            [["()[]{}"], ["(]"], ["{[()]}"]], [[""], ["(("], ["([)]"], [")("], ["([]){"], ["{[]}()"]],
            """
            public static class Solution
            {
                public static bool IsBalanced(string s)
                {
                    var st = new Stack<char>();
                    foreach (var c in s)
                    {
                        if (c is '(' or '[' or '{') { st.Push(c); continue; }
                        if (st.Count == 0) return false;
                        var o = st.Pop();
                        if ((c == ')' && o != '(') || (c == ']' && o != '[') || (c == '}' && o != '{')) return false;
                    }
                    return st.Count == 0;
                }
            }
            """,
            """
            public static class Solution
            {
                public static bool IsBalanced(string s)
                {
                    int depth = 0;
                    foreach (var c in s)
                    {
                        depth += c is '(' or '[' or '{' ? 1 : -1;
                        if (depth < 0) return false;
                    }
                    return depth == 0;
                }
            }
            """),

        new("spiral-order", "python", "spiral_order", "def spiral_order(matrix: list[list[int]]) -> list[int]",
            "Return all elements of a non-empty rectangular matrix in clockwise spiral order, starting at the top-left element.",
            (rng, c) =>
            {
                var (r, w) = (rng.Range(1, 2 + c), rng.Range(1, 2 + c));
                return [Enumerable.Range(0, r).Select(_ => RandInts(rng, w, 0, 99)).ToArray()];
            },
            a =>
            {
                var m = a[0].Deserialize<int[][]>()!; var o = new List<int>();
                int top = 0, bottom = m.Length - 1, left = 0, right = m[0].Length - 1;
                while (top <= bottom && left <= right)
                {
                    for (int j = left; j <= right; j++) o.Add(m[top][j]);
                    for (int i = top + 1; i <= bottom; i++) o.Add(m[i][right]);
                    if (top < bottom && left < right)
                    {
                        for (int j = right - 1; j >= left; j--) o.Add(m[bottom][j]);
                        for (int i = bottom - 1; i > top; i--) o.Add(m[i][left]);
                    }
                    top++; bottom--; left++; right--;
                }
                return o;
            },
            a =>
            {
                var m = a[0].Deserialize<int[][]>()!; int h = m.Length, w = m[0].Length;
                var seen = new bool[h, w]; var o = new List<int>();
                int[] dr = [0, 1, 0, -1], dc = [1, 0, -1, 0];
                int r = 0, c = 0, d = 0;
                for (int k = 0; k < h * w; k++)
                {
                    o.Add(m[r][c]); seen[r, c] = true;
                    int nr = r + dr[d], nc = c + dc[d];
                    if (nr < 0 || nr >= h || nc < 0 || nc >= w || seen[nr, nc]) { d = (d + 1) % 4; nr = r + dr[d]; nc = c + dc[d]; }
                    (r, c) = (nr, nc);
                }
                return o;
            },
            [[new[] { new[] { 1, 2, 3 }, new[] { 4, 5, 6 }, new[] { 7, 8, 9 } }], [new[] { new[] { 1, 2 }, new[] { 3, 4 }, new[] { 5, 6 } }]],
            [[new[] { new[] { 7 } }], [new[] { new[] { 1, 2, 3, 4 } }], [new[] { new[] { 1 }, new[] { 2 }, new[] { 3 } }], [new[] { new[] { 1, 2, 3 }, new[] { 4, 5, 6 } }], [new[] { new[] { 1, 2, 3, 4 }, new[] { 5, 6, 7, 8 }, new[] { 9, 10, 11, 12 } }]],
            """
            def spiral_order(matrix):
                out = []
                top, bottom, left, right = 0, len(matrix) - 1, 0, len(matrix[0]) - 1
                while top <= bottom and left <= right:
                    for j in range(left, right + 1):
                        out.append(matrix[top][j])
                    for i in range(top + 1, bottom + 1):
                        out.append(matrix[i][right])
                    if top < bottom and left < right:
                        for j in range(right - 1, left - 1, -1):
                            out.append(matrix[bottom][j])
                        for i in range(bottom - 1, top, -1):
                            out.append(matrix[i][left])
                    top += 1
                    bottom -= 1
                    left += 1
                    right -= 1
                return out
            """,
            """
            def spiral_order(matrix):
                out = []
                top, bottom, left, right = 0, len(matrix) - 1, 0, len(matrix[0]) - 1
                while top <= bottom and left <= right:
                    for j in range(left, right + 1):
                        out.append(matrix[top][j])
                    for i in range(top + 1, bottom + 1):
                        out.append(matrix[i][right])
                    for j in range(right - 1, left - 1, -1):
                        out.append(matrix[bottom][j])
                    for i in range(bottom - 1, top, -1):
                        out.append(matrix[i][left])
                    top += 1
                    bottom -= 1
                    left += 1
                    right -= 1
                return out
            """),

        new("kth-smallest", "python", "kth_smallest", "def kth_smallest(nums: list[int], k: int) -> int",
            "Return the k-th smallest value (k is 1-based, duplicates count separately) of a non-empty list; 1 <= k <= len(nums).",
            (rng, c) => { var n = rng.Range(1, 5 + 3 * c); return [RandInts(rng, n, 0, 20), rng.Range(1, n)]; },
            a => Ints(a[0]).Order().ToArray()[Int(a[1]) - 1],
            a =>
            {
                var xs = Ints(a[0]).ToList(); var k = Int(a[1]) - 1;
                while (true)
                {
                    var p = xs[xs.Count / 2]; var lo = xs.Where(x => x < p).ToList(); var eq = xs.Count(x => x == p);
                    if (k < lo.Count) xs = lo;
                    else if (k < lo.Count + eq) return p;
                    else { k -= lo.Count + eq; xs = xs.Where(x => x > p).ToList(); }
                }
            },
            [[new[] { 3, 1, 2 }, 2], [new[] { 5, 5, 1 }, 3]], [[new[] { 4 }, 1], [new[] { 2, 2, 2 }, 2], [new[] { 9, -1, 7, 3 }, 4], [new[] { 9, -1, 7, 3 }, 1]],
            """
            def kth_smallest(nums, k):
                return sorted(nums)[k - 1]
            """,
            """
            def kth_smallest(nums, k):
                return sorted(nums)[k]
            """,
            new PerfCase(rng => { var n = 50_000; return [RandInts(rng, n, 0, 1_000_000), n / 2]; }, 3000,
                """
                def kth_smallest(nums, k):
                    a = list(nums)
                    for _ in range(k - 1):
                        a.remove(min(a))
                    return min(a)
                """),
            "heapq"),

        new("merge-intervals", "python", "merge_intervals", "def merge_intervals(intervals: list[list[int]]) -> list[list[int]]",
            "Merge all overlapping closed intervals [a, b] (a < b) and return the merged intervals sorted by start. Intervals that merely touch (one ends where the next begins) are merged too.",
            (rng, c) => [RandIntervals(rng, rng.Range(1, 4 + 2 * c), 30, 8)],
            a =>
            {
                var o = new List<int[]>();
                foreach (var iv in a[0].Deserialize<int[][]>()!.OrderBy(x => x[0]).ThenBy(x => x[1]))
                    if (o.Count > 0 && iv[0] <= o[^1][1]) o[^1][1] = Math.Max(o[^1][1], iv[1]); else o.Add([iv[0], iv[1]]);
                return o;
            },
            a =>
            {
                var ivs = a[0].Deserialize<int[][]>()!; var cover = new bool[ivs.Max(x => x[1]) + 1];
                foreach (var iv in ivs) for (int u = iv[0]; u < iv[1]; u++) cover[u] = true; // unit segment u covers [u, u+1]
                var o = new List<int[]>();
                for (int u = 0; u < cover.Length; u++)
                {
                    if (!cover[u]) continue;
                    int s = u; while (u + 1 < cover.Length && cover[u + 1]) u++;
                    o.Add([s, u + 1]);
                }
                return o;
            },
            [[new[] { new[] { 1, 3 }, new[] { 2, 6 }, new[] { 8, 10 } }], [new[] { new[] { 5, 7 }, new[] { 1, 2 } }]],
            [[new[] { new[] { 1, 2 }, new[] { 2, 3 } }], [new[] { new[] { 4, 5 } }], [new[] { new[] { 1, 10 }, new[] { 2, 3 }, new[] { 4, 5 } }], [new[] { new[] { 6, 8 }, new[] { 1, 9 }, new[] { 2, 4 }, new[] { 4, 7 } }]],
            """
            def merge_intervals(intervals):
                out = []
                for a, b in sorted(intervals):
                    if out and a <= out[-1][1]:
                        out[-1][1] = max(out[-1][1], b)
                    else:
                        out.append([a, b])
                return out
            """,
            """
            def merge_intervals(intervals):
                out = []
                for a, b in sorted(intervals):
                    if out and a < out[-1][1]:
                        out[-1][1] = max(out[-1][1], b)
                    else:
                        out.append([a, b])
                return out
            """,
            new PerfCase(rng => [RandIntervals(rng, 20_000, 10_000_000, 50)], 3000,
                """
                def merge_intervals(intervals):
                    out = []
                    for a, b in intervals:
                        keep = []
                        for c, d in out:
                            if c <= b and a <= d:
                                a, b = min(a, c), max(b, d)
                            else:
                                keep.append([c, d])
                        keep.append([a, b])
                        out = keep
                    return sorted(out)
                """),
            "itertools"),

        new("two-sum", "python", "two_sum", "def two_sum(nums: list[int], target: int) -> list[int]",
            "The list holds distinct integers and exactly one pair of different positions i < j has nums[i] + nums[j] == target. Return [i, j] (0-based).",
            (rng, c) => TwoSumArgs(rng, rng.Range(2, 6 + 2 * c), 60, false),
            a =>
            {
                var xs = Ints(a[0]); var t = Int(a[1]); var pos = new Dictionary<int, int>();
                for (int i = 0; i < xs.Length; i++) { if (pos.TryGetValue(t - xs[i], out var j)) return new[] { j, i }; pos[xs[i]] = i; }
                throw new InvalidOperationException("no pair");
            },
            a =>
            {
                var xs = Ints(a[0]); var t = Int(a[1]); var idx = Enumerable.Range(0, xs.Length).OrderBy(i => xs[i]).ToArray();
                int lo = 0, hi = idx.Length - 1;
                while (lo < hi) { var s = xs[idx[lo]] + xs[idx[hi]]; if (s == t) return new[] { Math.Min(idx[lo], idx[hi]), Math.Max(idx[lo], idx[hi]) }; if (s < t) lo++; else hi--; }
                throw new InvalidOperationException("no pair");
            },
            [[new[] { 2, 7, 11, 15 }, 9], [new[] { 3, 2, 4 }, 6]], [[new[] { 1, 5 }, 6], [new[] { 8, 3, 9, 1 }, 4], [new[] { 0, 4, 3, 9 }, 7], [new[] { 10, 20, 30, 40, 50 }, 90]],
            """
            def two_sum(nums, target):
                seen = {}
                for i, x in enumerate(nums):
                    if target - x in seen:
                        return [seen[target - x], i]
                    seen[x] = i
                return []
            """,
            """
            def two_sum(nums, target):
                seen = {}
                for i, x in enumerate(nums):
                    if target - x in seen:
                        return [seen[target - x] + 1, i + 1]
                    seen[x] = i
                return []
            """,
            new PerfCase(rng => TwoSumArgs(rng, 30_000, 10_000_000, true), 3000,
                """
                def two_sum(nums, target):
                    for i in range(len(nums)):
                        for j in range(i + 1, len(nums)):
                            if nums[i] + nums[j] == target:
                                return [i, j]
                    return []
                """),
            "itertools"),

        new("top-k-frequent", "python", "top_k_frequent", "def top_k_frequent(nums: list[int], k: int) -> list[int]",
            "Return the k most frequent distinct values, ordered by frequency descending; values with equal frequency are ordered by value ascending. 1 <= k <= number of distinct values.",
            (rng, c) =>
            {
                var xs = RandInts(rng, rng.Range(1, 8 + 3 * c), 0, 6);
                return [xs, rng.Range(1, Math.Min(3, xs.Distinct().Count()))];
            },
            a => Ints(a[0]).GroupBy(x => x).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Take(Int(a[1])).Select(g => g.Key).ToArray(),
            a =>
            {
                var xs = Ints(a[0]); var counts = new int[xs.Max() + 1]; foreach (var x in xs) counts[x]++;
                var o = new List<int>();
                for (int r = 0; r < Int(a[1]); r++)
                {
                    int best = -1;
                    for (int v = 0; v < counts.Length; v++) if (counts[v] > 0 && (best < 0 || counts[v] > counts[best])) best = v;
                    o.Add(best); counts[best] = 0;
                }
                return o;
            },
            [[new[] { 1, 1, 1, 2, 2, 3 }, 2], [new[] { 4, 4, 5 }, 1]], [[new[] { 3, 3, 1, 1, 2 }, 2], [new[] { 5 }, 1], [new[] { 2, 1, 2, 1, 3, 3 }, 3], [new[] { 0, 0, 0, 7, 7, 9, 9, 9 }, 2]],
            """
            def top_k_frequent(nums, k):
                counts = {}
                for x in nums:
                    counts[x] = counts.get(x, 0) + 1
                return sorted(counts, key=lambda x: (-counts[x], x))[:k]
            """,
            """
            def top_k_frequent(nums, k):
                counts = {}
                for x in nums:
                    counts[x] = counts.get(x, 0) + 1
                return sorted(counts, key=lambda x: -counts[x])[:k]
            """,
            new PerfCase(rng => [RandInts(rng, 60_000, 0, 49_999), 10], 3000,
                """
                def top_k_frequent(nums, k):
                    uniq = []
                    for x in nums:
                        if x not in uniq:
                            uniq.append(x)
                    return sorted(uniq, key=lambda x: (-nums.count(x), x))[:k]
                """),
            "collections"),
    ];
}
