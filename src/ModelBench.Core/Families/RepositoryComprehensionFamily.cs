using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelBench.Core.Domain;
using ModelBench.Core.Generation;
using ModelBench.Core.Grading;

namespace ModelBench.Core.Families;

/// <summary>
/// A seeded synthetic Python-flavoured repository (never executed) with 3..20 files: config.py plus modules under app/.
/// Functions form an acyclic call graph with imports; config constants are overridden by loaded modules.
/// One question per task: transitive callers, effective config value, defining file, or the unique call chain between two functions.
/// </summary>
public sealed partial class RepositoryComprehensionFamily : IBenchmarkFamily
{
    public string Id => "repository-comprehension";
    public int Version => 1;
    public string Category => "repository-comprehension";
    public string Description => "Questions about a synthetic multi-file repository: call graphs, config overrides, definitions.";
    public ExecutionMode Mode => ExecutionMode.SingleTurn;

    private static readonly int[] FileCounts = [3, 5, 8, 13, 20];
    private static readonly string[] ModuleWords = ["alpha", "beta", "gamma", "delta", "omega", "sigma", "kappa", "lambda", "theta", "zeta", "iota", "nova", "orbit", "ember", "quartz", "harbor", "falcon", "meadow", "cobalt", "tundra", "willow", "granite", "saffron"];
    private static readonly string[] Verbs = ["load", "parse", "check", "build", "render", "fetch", "merge", "split", "scan", "apply", "emit", "flush", "sort", "pack", "sync", "probe"];
    private static readonly string[] Nouns = ["cache", "index", "queue", "token", "frame", "block", "route", "entry", "stream", "table"];
    private static readonly string[] Constants = ["CONFIG_TIMEOUT", "CONFIG_RETRIES", "CONFIG_BATCH", "CONFIG_LIMIT", "CONFIG_DEPTH", "CONFIG_WORKERS", "CONFIG_BUFFER", "CONFIG_WINDOW"];

    /// <summary>Kind: callers | config | defined-in | chain.</summary>
    private sealed record Ref(string Kind, string Target, string? Target2 = null);

    // ---------------------------------------------------------------- generation

    public GeneratedTask Generate(Rng rng, int c)
    {
        if (c is < Complexity.Min or > Complexity.Max) throw new ArgumentOutOfRangeException(nameof(c));
        var modCount = FileCounts[c - 1] - 1;
        var modNames = Shuffled(rng, ModuleWords).Take(modCount).ToArray();

        // functions, numbered globally module by module; calls only go to higher numbers, so the graph is acyclic
        var perModule = Enumerable.Range(0, modCount).Select(_ => rng.Range(2, 2 + c / 3)).ToArray();
        var total = perModule.Sum();
        var names = Shuffled(rng, Verbs.SelectMany(v => Nouns.Select(n => $"{v}_{n}")).ToList()).Take(total).ToArray();
        var modOf = perModule.SelectMany((k, m) => Enumerable.Repeat(m, k)).ToArray();
        var calls = Enumerable.Range(0, total).Select(_ => new List<int>()).ToArray();
        for (var i = 0; i < total - 1; i++)
            for (var k = rng.Range(1, 1 + (c + 1) / 2); k > 0; k--)
            {
                var j = i + rng.Range(1, Math.Min(total - 1 - i, 5));
                if (!calls[i].Contains(j)) calls[i].Add(j);
            }

        // config: constants, per-module overrides, and the modules that are actually loaded
        var consts = Shuffled(rng, Constants).Take(2 + c).ToArray();
        var baseValue = consts.Select(_ => rng.Range(1, 99)).ToArray();
        var overrides = Enumerable.Range(0, modCount).Select(_ => new List<(int K, int V)>()).ToArray();
        for (var m = 0; m < modCount; m++)
            for (var pass = 0; pass < (c >= 4 ? 2 : 1); pass++)
            {
                var k = rng.Next(consts.Length);
                if (rng.Chance(0.5) && overrides[m].All(o => o.K != k)) overrides[m].Add((k, rng.Range(1, 99)));
            }
        var loaded = Shuffled(rng, Enumerable.Range(0, modCount).ToList()).Take(Math.Max(1, (modCount * 3 + 3) / 4)).ToList();
        if (loaded.All(m => overrides[m].Count == 0)) overrides[loaded[0]].Add((rng.Next(consts.Length), rng.Range(1, 99)));
        var effective = (int[])baseValue.Clone();
        var touched = new bool[consts.Length];
        foreach (var m in loaded)
            foreach (var (k, v) in overrides[m]) { effective[k] = v; touched[k] = true; }

        var files = new List<(string Path, string Text)> { ("config.py", ConfigText(consts, baseValue, loaded.Select(m => modNames[m]).ToArray())) };
        for (var m = 0; m < modCount; m++)
            files.Add(($"app/{modNames[m]}.py", ModuleText(rng, c, m, modNames, names, modOf, calls, consts, overrides[m])));

        // question
        var (r, expected, bad) = rng.Next(4) switch
        {
            0 => Callers(rng, names, calls),
            1 => Config(rng, consts, baseValue, effective, touched),
            2 => DefinedIn(rng, names, modOf, modNames),
            _ => Chain(rng, c, names, calls),
        };
        var user = Render(files, r);
        var unordered = r.Kind == "callers";
        var hidden = new TaskHidden(
            [new GraderSpec("structural", "answer", JsonUtil.Element(new { expected, unorderedArrays = unordered }))],
            null, JsonUtil.Element(r));
        IReadOnlyList<ChatMessage> Turn(string t) => [new ChatMessage(Role.Assistant, t)];
        return new GeneratedTask(TaskPrompt.Simple(user, AnswerFormat.Json), hidden,
            [new ChatMessage(Role.Assistant, $"Tracing the code...\n<answer>{JsonUtil.Serialize(expected)}</answer>")],
            bad.Select(b => Turn($"<answer>{JsonUtil.Serialize(b)}</answer>")).Append(Turn("I looked through the files but cannot give a definite answer.")).ToList());
    }

    private static List<T> Shuffled<T>(Rng rng, IEnumerable<T> items)
    {
        var l = items.ToList();
        rng.Shuffle(l);
        return l;
    }

    private static string ConfigText(string[] consts, int[] values, string[] loadedModules)
    {
        var sb = new StringBuilder("# Application settings.\n");
        for (var k = 0; k < consts.Length; k++) sb.Append($"{consts[k]} = {values[k]}\n");
        return sb.Append($"\nLOAD_ORDER = [{string.Join(", ", loadedModules.Select(n => $"\"app.{n}\""))}]").ToString();
    }

    private static string ModuleText(Rng rng, int c, int m, string[] modNames, string[] names, int[] modOf, List<int>[] calls, string[] consts, List<(int K, int V)> overrides)
    {
        var own = Enumerable.Range(0, names.Length).Where(f => modOf[f] == m).ToList();
        var sb = new StringBuilder($"# Module {modNames[m]}.\n");
        var imports = own.SelectMany(f => calls[f]).Where(g => modOf[g] != m).Distinct()
            .GroupBy(g => modOf[g]).OrderBy(g => modNames[g.Key], StringComparer.Ordinal);
        foreach (var grp in imports) sb.Append($"from app.{modNames[grp.Key]} import {string.Join(", ", grp.Select(g => names[g]).Order(StringComparer.Ordinal))}\n");
        if (overrides.Count > 0)
        {
            sb.Append("import config\n\n");
            foreach (var (k, v) in overrides) sb.Append($"config.{consts[k]} = {v}\n");
        }
        foreach (var f in own)
        {
            sb.Append($"\n\ndef {names[f]}(x):\n");
            if (c >= 3 && rng.Chance(0.5)) sb.Append($"    # the old version used {names[rng.Next(names.Length)]}() here\n");
            var prev = "x";
            for (var i = 0; i < calls[f].Count; i++)
            {
                sb.Append($"    r{i} = {names[calls[f][i]]}({prev})\n");
                prev = $"r{i}";
            }
            sb.Append($"    return {prev}");
        }
        return sb.ToString();
    }

    private static (Ref, object, object[]) Callers(Rng rng, string[] names, List<int>[] calls)
    {
        var called = Enumerable.Range(0, names.Length).Where(f => calls.Any(cs => cs.Contains(f))).ToList();
        var target = rng.Pick(called);
        var found = new HashSet<int>();
        var frontier = new Stack<int>([target]);
        while (frontier.Count > 0)
        {
            var t = frontier.Pop();
            for (var f = 0; f < names.Length; f++)
                if (calls[f].Contains(t) && found.Add(f)) frontier.Push(f);
        }
        var answer = found.Select(f => names[f]).Order(StringComparer.Ordinal).ToArray();
        return (new Ref("callers", names[target]), answer,
            [answer.Skip(1).ToArray(), answer.Append(names[target]).Order(StringComparer.Ordinal).ToArray()]);
    }

    private static (Ref, object, object[]) Config(Rng rng, string[] consts, int[] baseValue, int[] effective, bool[] touched)
    {
        var k = rng.Pick(Enumerable.Range(0, consts.Length).Where(i => touched[i]).ToList());
        return (new Ref("config", consts[k]), effective[k],
            [effective[k] + 1, baseValue[k] != effective[k] ? baseValue[k] : effective[k] + 2]);
    }

    private static (Ref, object, object[]) DefinedIn(Rng rng, string[] names, int[] modOf, string[] modNames)
    {
        var f = rng.Next(names.Length);
        var path = $"app/{modNames[modOf[f]]}.py";
        var other = $"app/{modNames[(modOf[f] + 1) % modNames.Length]}.py";
        return (new Ref("defined-in", names[f]), path, [other, $"{modNames[modOf[f]]}.py"]);
    }

    private static (Ref, object, object[]) Chain(Rng rng, int c, string[] names, List<int>[] calls)
    {
        var n = names.Length;
        var paths = new long[n, n]; // number of distinct call paths from i to j
        for (var i = n - 1; i >= 0; i--)
        {
            paths[i, i] = 1;
            foreach (var k in calls[i])
                for (var j = 0; j < n; j++) paths[i, j] += paths[k, j];
        }
        List<int> Walk(int i, int j)
        {
            var chain = new List<int> { i };
            while (i != j) { i = calls[i].First(k => paths[k, j] > 0); chain.Add(i); }
            return chain;
        }
        var unique = new List<List<int>>();
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
                if (paths[i, j] == 1) unique.Add(Walk(i, j));
        var minLen = 2 + (c - 1) / 2;
        var long_ = unique.Where(p => p.Count >= minLen).ToList();
        var chain = rng.Pick(long_.Count > 0 ? long_ : unique).Select(f => names[f]).ToArray();
        return (new Ref("chain", chain[0], chain[^1]), chain,
            [Enumerable.Reverse(chain).ToArray(), chain[..^1]]);
    }

    // ---------------------------------------------------------------- prompt

    private static string Question(Ref r) => r.Kind switch
    {
        "callers" => $"Question: which functions call `{r.Target}`, either directly or through a chain of other calls? Give a JSON array of function names (any order, no duplicates) inside <answer></answer> tags, for example <answer>[\"name_one\",\"name_two\"]</answer>.",
        "config" => $"Question: what is the effective value of `{r.Target}` after the overrides of all loaded modules have been applied? Give it as a JSON integer inside <answer></answer> tags, for example <answer>42</answer>.",
        "defined-in" => $"Question: in which file is the function `{r.Target}` defined? Give the path exactly as it appears in the file headers, as a JSON string inside <answer></answer> tags, for example <answer>\"app/example.py\"</answer>.",
        _ => $"Question: give the call chain from `{r.Target}` to `{r.Target2}`: a JSON array of function names that starts with `{r.Target}`, ends with `{r.Target2}`, and in which every function directly calls the next. Exactly one such chain exists. Put it inside <answer></answer> tags, for example <answer>[\"first_fn\",\"second_fn\"]</answer>.",
    };

    private static string Render(List<(string Path, string Text)> files, Ref r)
    {
        var sb = new StringBuilder("Below is a small Python repository. It is only to be read, never run.\n\n"
            + "Rules: `config.py` defines the default value of each setting. `LOAD_ORDER` lists the only modules that are loaded, in order. "
            + "A loaded module may assign `config.NAME = value` at the top level of its file, and later assignments win. "
            + "Modules that are not in `LOAD_ORDER` are never loaded, so their assignments have no effect. Text after `#` is a comment. Function names are unique across the repository.\n");
        foreach (var (path, text) in files) sb.Append($"\n### `{path}`\n```python\n{text}\n```\n");
        return sb.Append('\n').Append(Question(r)).ToString();
    }

    // ---------------------------------------------------------------- independent validation: re-analyse the rendered text

    [GeneratedRegex(@"^### `([^`]+)`\r?\n```python\r?\n(.*?)\r?\n```", RegexOptions.Singleline | RegexOptions.Multiline)] private static partial Regex FileBlock();
    [GeneratedRegex(@"^def (\w+)\(")] private static partial Regex Def();
    [GeneratedRegex(@"\b(\w+)\(")] private static partial Regex CallSite();
    [GeneratedRegex(@"^(CONFIG_\w+) = (\d+)$")] private static partial Regex Base();
    [GeneratedRegex(@"^config\.(CONFIG_\w+) = (\d+)$")] private static partial Regex Override();

    public ValidationResult Validate(GeneratedTask task)
    {
        var problems = new List<string>();
        try
        {
            if (task.Prompt.AnswerFormat != AnswerFormat.Json) problems.Add("answer format must be Json");
            var r = JsonUtil.Deserialize<Ref>(task.Hidden.Reference!.Value.GetRawText());
            if (!task.Prompt.User.EndsWith(Question(r), StringComparison.Ordinal)) problems.Add("prompt does not end with the question from the reference");
            var expected = JsonSerializer.Serialize(Answer(task.Prompt.User, r));
            var graderSpec = task.Hidden.Graders.Single();
            var stated = graderSpec.Config.GetProperty("expected").GetRawText();
            if (stated != expected) problems.Add($"expected {stated} != answer re-derived from the rendered repository {expected}");
            if (Last(task.ReferenceTurns.Single().Content) != expected) problems.Add("reference turn does not give the expected answer");
            if (task.KnownBadTurns.Count < 2) problems.Add("need at least two known-bad turns");
            foreach (var bad in task.KnownBadTurns)
                if (Last(bad.Last().Content) == expected) problems.Add("a known-bad turn contains the correct answer");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            problems.Add($"malformed task: {ex.Message}");
        }
        return problems.Count == 0 ? ValidationResult.Valid : new ValidationResult(false, problems);
    }

    private static string? Last(string? text)
    {
        var a = AnswerExtractor.Extract(text is null ? null : AnswerExtractor.StripReasoning(text), AnswerFormat.Json);
        return a is null ? null : JsonSerializer.Serialize(JsonDocument.Parse(a).RootElement); // sorted callers are already in canonical order
    }

    /// <summary>Parses the files out of the prompt text and answers the question from scratch.</summary>
    private static object Answer(string prompt, Ref r)
    {
        var files = FileBlock().Matches(prompt).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ReplaceLineEndings("\n").Split('\n'));
        var definedIn = new Dictionary<string, string>();
        var calls = new Dictionary<string, List<string>>();
        foreach (var (path, lines) in files)
            foreach (var line in lines)
                if (Def().Match(line) is { Success: true } d) definedIn[d.Groups[1].Value] = path;
        foreach (var (path, lines) in files)
        {
            string? current = null;
            foreach (var line in lines)
            {
                if (Def().Match(line) is { Success: true } d) { current = d.Groups[1].Value; calls[current] = []; continue; }
                if (line.Length > 0 && !char.IsWhiteSpace(line[0])) current = null;
                if (current is null) continue;
                var code = line.Split('#')[0];
                foreach (Match call in CallSite().Matches(code))
                    if (definedIn.ContainsKey(call.Groups[1].Value) && !calls[current].Contains(call.Groups[1].Value)) calls[current].Add(call.Groups[1].Value);
            }
        }
        switch (r.Kind)
        {
            case "defined-in": return definedIn[r.Target];
            case "config":
            {
                var value = int.Parse(files["config.py"].Select(l => Base().Match(l)).First(m => m.Success && m.Groups[1].Value == r.Target).Groups[2].Value);
                var order = Regex.Matches(files["config.py"].Single(l => l.StartsWith("LOAD_ORDER")), "\"app\\.(\\w+)\"").Select(m => $"app/{m.Groups[1].Value}.py");
                foreach (var path in order)
                    foreach (var l in files[path])
                        if (Override().Match(l) is { Success: true } o && o.Groups[1].Value == r.Target) value = int.Parse(o.Groups[2].Value);
                return value;
            }
            case "callers":
            {
                var found = new List<string>();
                for (var grew = true; grew;)
                {
                    grew = false;
                    foreach (var (caller, callees) in calls)
                        if (!found.Contains(caller) && callees.Any(t => t == r.Target || found.Contains(t))) { found.Add(caller); grew = true; }
                }
                return found.Order(StringComparer.Ordinal).ToArray();
            }
            default:
            {
                var all = new List<List<string>>();
                void Dfs(List<string> path)
                {
                    if (path[^1] == r.Target2) { all.Add([.. path]); return; }
                    foreach (var next in calls[path[^1]]) Dfs([.. path, next]);
                }
                Dfs([r.Target]);
                return all.Count == 1 ? all[0].ToArray() : throw new InvalidOperationException($"{all.Count} call chains from {r.Target} to {r.Target2}, expected exactly one");
            }
        }
    }
}
