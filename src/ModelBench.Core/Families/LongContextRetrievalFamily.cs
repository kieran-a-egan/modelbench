using System.Text;
using System.Text.RegularExpressions;
using ModelBench.Core.Domain;
using ModelBench.Core.Generation;

namespace ModelBench.Core.Families;

/// <summary>
/// A synthetic staff registry (2k..120k characters) where every person has a locker line, a reports-to line and a badge line.
/// The question needs 1 (c1-2), 2 (c3) or 3 (c4-5) of those lines chained; near-miss locker numbers and near-miss names act as distractors.
/// </summary>
public sealed partial class LongContextRetrievalFamily : IBenchmarkFamily
{
    public string Id => "long-context-retrieval";
    public int Version => 1;
    public string Category => "long-context-retrieval";
    public string Description => "Find a badge code in a long synthetic registry, following 1-3 linked records past near-miss distractors.";
    public ExecutionMode Mode => ExecutionMode.SingleTurn;

    private static readonly int[] Sizes = [2_000, 8_000, 32_000, 64_000, 120_000];
    private static readonly string[] F1 = ["Ma", "Jo", "El", "Ka", "Ri", "Bo", "Le", "Da", "Ni", "Su", "Te", "Vi"];
    private static readonly string[] F2 = ["rek", "lan", "dra", "nis", "vor", "mon", "tia", "dor", "lex", "ren", "sa", "bel"];
    private static readonly string[] L1 = ["Ok", "Tol", "Har", "Wen", "Bran", "Mor", "Per", "Sal", "Gar", "Fen", "Dun", "Lor"];
    private static readonly string[] L2 = ["afor", "vane", "ley", "dahl", "wick", "ston", "ford", "well", "mont", "ski", "berg", "ton"];
    private const string Begin = "--- BEGIN REGISTRY ---", End = "--- END REGISTRY ---";

    private sealed class Person
    {
        public string Name = "", Code = "", Boss = "";
        public int Locker;
    }

    private sealed record Ref(int Complexity, string[] NearCodes, string[] NearLockers);

    [GeneratedRegex(@"^R\d+ \| Locker (\d{4}) is assigned to (.+)\.$", RegexOptions.Multiline)] private static partial Regex LockerLine();
    [GeneratedRegex(@"^R\d+ \| (.+) reports to (.+)\.$", RegexOptions.Multiline)] private static partial Regex BossLine();
    [GeneratedRegex(@"^R\d+ \| Badge code for (.+) is ([A-Z]{2}-\d{4})\.$", RegexOptions.Multiline)] private static partial Regex BadgeLine();

    public GeneratedTask Generate(Rng rng, int c)
    {
        if (c is < Complexity.Min or > Complexity.Max) throw new ArgumentOutOfRangeException(nameof(c));
        var hops = c <= 2 ? 1 : c == 3 ? 2 : 3;
        var names = new HashSet<string>();
        var lockers = new HashSet<int>();
        var codes = new HashSet<string>();
        var people = new List<Person>();

        string NewName(string n) => names.Add(n) ? n : "";
        Person Add(string name, int locker = 0)
        {
            while (name == "") name = NewName($"{rng.Pick(F1)}{rng.Pick(F2)} {rng.Pick(L1)}{rng.Pick(L2)}");
            while (locker == 0 || !lockers.Add(locker)) locker = rng.Range(1000, 9999);
            string code;
            do code = $"{(char)('A' + rng.Next(26))}{(char)('A' + rng.Next(26))}-{rng.Range(1000, 9999)}"; while (!codes.Add(code));
            var p = new Person { Name = name, Locker = locker, Code = code };
            p.Boss = people.Count > 0 ? rng.Pick(people).Name : "";
            people.Add(p);
            return p;
        }

        var target = Add(""); // the person whose badge code is asked for
        var owner = target;
        string[] nearLockers = [];
        if (hops == 3) owner = Add("");
        if (hops >= 2)
        {
            var l = owner.Locker;
            var s = l.ToString();
            nearLockers = new[] { $"{s[1]}{s[0]}{s[2..]}", s[..3] + (s[3] == '9' ? '1' : (char)(s[3] + 1)), (s[0] == '9' ? '1' : (char)(s[0] + 1)) + s[1..] }
                .Where(x => x != s && x[0] != '0').Distinct().ToArray();
            foreach (var nl in nearLockers) Add("", int.Parse(nl));
        }
        // near-miss names: same first name with another surname ending, same surname with another first-name ending
        var (first, last) = (target.Name.Split(' ')[0], target.Name.Split(' ')[1]);
        var near = new List<Person>();
        while (near.Count < 2)
        {
            var n = NewName(near.Count == 0 ? $"{first} {rng.Pick(L1)}{rng.Pick(L2)}" : $"{rng.Pick(F1)}{rng.Pick(F2)} {last}");
            if (n != "") near.Add(Add(n));
        }
        if (hops == 3) owner.Boss = target.Name;
        target.Boss = people[hops == 3 ? 2 : 1].Name;

        string[] Lines() => people.SelectMany(p => new[]
        {
            $"Locker {p.Locker} is assigned to {p.Name}.",
            $"{p.Name} reports to {p.Boss}.",
            $"Badge code for {p.Name} is {p.Code}.",
        }).ToArray();
        var all = Lines();
        while (all.Sum(l => l.Length + 10) < Sizes[c - 1])
        {
            var need = (Sizes[c - 1] - all.Sum(l => l.Length + 10)) / 140 + 1;
            for (var i = 0; i < need; i++) Add("");
            all = Lines();
        }
        var shuffled = all.ToList();
        rng.Shuffle(shuffled);
        var body = string.Join("\n", shuffled.Select((l, i) => $"R{i + 1:00000} | {l}"));

        var question = hops switch
        {
            1 => $"What is the badge code of {target.Name}?",
            2 => $"What is the badge code of the person who owns locker {owner.Locker}?",
            _ => $"What is the badge code of the person that the owner of locker {owner.Locker} reports to?",
        };
        var user = $"The registry below lists staff records. Every person has exactly one locker line, one \"reports to\" line and one badge-code line, " +
                   $"and every locker number and badge code is used by only one person.\n\n{Begin}\n{body}\n{End}\n\n" +
                   $"Question: {question}\nReply with the badge code only, in the form AB-1234, inside <answer></answer> tags.";
        var hidden = new TaskHidden(
            [new GraderSpec("exact", "badge-code", JsonUtil.Element(new { expected = target.Code, normalize = "trim" }))],
            null, JsonUtil.Element(new Ref(c, near.Select(p => p.Code).ToArray(), nearLockers)));
        var bad = new List<IReadOnlyList<ChatMessage>>
        {
            Turn($"<answer>{near[0].Code}</answer>"),
            Turn($"The badge code is {target.Code}."),
            Turn($"<answer>{near[1].Code}</answer>"),
        };
        return new GeneratedTask(TaskPrompt.Simple(user), hidden, Turn($"<answer>{target.Code}</answer>"), bad);
    }

    private static IReadOnlyList<ChatMessage> Turn(string s) => [new ChatMessage(Role.Assistant, s)];

    // ---------------------------------------------------------------- independent validation

    public ValidationResult Validate(GeneratedTask task)
    {
        var problems = new List<string>();
        try
        {
            var p = task.Prompt.User;
            if (task.Prompt.AnswerFormat != AnswerFormat.Tagged) problems.Add("answer format must be Tagged");
            if (!p.Contains("<answer></answer>") || !p.Contains("AB-1234")) problems.Add("prompt does not state the answer format");
            var r = JsonUtil.Deserialize<Ref>(task.Hidden.Reference!.Value.GetRawText());
            var expected = task.Hidden.Graders.Single(g => g.Type == "exact").Config.GetProperty("expected").GetString();

            var (a, b) = (p.IndexOf(Begin, StringComparison.Ordinal), p.IndexOf(End, StringComparison.Ordinal));
            if (a < 0 || b < a) return ValidationResult.Fail("registry markers missing");
            var haystack = p[a..b];
            if (haystack.Length < Sizes[r.Complexity - 1]) problems.Add($"haystack {haystack.Length} chars is below {Sizes[r.Complexity - 1]}");

            var lockers = LockerLine().Matches(haystack).Select(m => (Num: m.Groups[1].Value, Who: m.Groups[2].Value)).ToList();
            var bosses = BossLine().Matches(haystack).Select(m => (Who: m.Groups[1].Value, Boss: m.Groups[2].Value)).ToList();
            var badges = BadgeLine().Matches(haystack).Select(m => (Who: m.Groups[1].Value, Code: m.Groups[2].Value)).ToList();
            if (lockers.Count != bosses.Count || lockers.Count != badges.Count) problems.Add("record types are unbalanced");

            // Resolve the question by scanning, from its wording alone.
            string? subject;
            string? Unique<T>(IEnumerable<T> hits, Func<T, string> pick, string what)
            {
                var l = hits.ToList();
                if (l.Count != 1) { problems.Add($"{what} matched {l.Count} records"); return null; }
                return pick(l[0]);
            }
            string Q(string rx) => Regex.Match(p, rx).Groups[1].Value;
            if (p.Contains("that the owner of locker "))
            {
                var key = Q(@"owner of locker (\d{4}) reports to");
                var owner = Unique(lockers.Where(x => x.Num == key), x => x.Who, $"locker {key}");
                subject = owner is null ? null : Unique(bosses.Where(x => x.Who == owner), x => x.Boss, $"boss of {owner}");
            }
            else if (p.Contains("who owns locker "))
            {
                var key = Q(@"owns locker (\d{4})\?");
                subject = Unique(lockers.Where(x => x.Num == key), x => x.Who, $"locker {key}");
            }
            else subject = Q(@"badge code of ([A-Za-z ]+)\?");
            var found = subject is null ? null : Unique(badges.Where(x => x.Who == subject), x => x.Code, $"badge of {subject}");
            if (found != expected) problems.Add($"scan found {found}, expected {expected}");
            if (expected is not null && badges.Count(x => x.Code == expected) != 1) problems.Add("expected code is not unique");

            foreach (var nl in r.NearLockers)
                if (nl == Q(@"locker (\d{4})") || lockers.Count(x => x.Num == nl) != 1) problems.Add($"near-miss locker {nl} collides");
            foreach (var nc in r.NearCodes)
                if (nc == expected || badges.Count(x => x.Code == nc) != 1) problems.Add($"near-miss code {nc} collides");
            if (badges.Select(x => x.Who).Distinct().Count() != badges.Count) problems.Add("a person has two badge lines");
            if (task.KnownBadTurns.Count < 2) problems.Add("need at least two known-bad turns");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            problems.Add($"malformed task: {ex.Message}");
        }
        return problems.Count == 0 ? ValidationResult.Valid : new ValidationResult(false, problems);
    }
}
