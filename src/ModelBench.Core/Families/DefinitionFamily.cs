using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ModelBench.Core.Domain;
using ModelBench.Core.Generation;
using ModelBench.Core.Grading;

namespace ModelBench.Core.Families;

/// <summary>
/// A family defined by files: `benchmarks/static/*.json` (id `static:&lt;id&gt;`) or `benchmarks/repos/&lt;name&gt;/benchmark.json` (id `repo:&lt;name&gt;`).
/// Task N of a suite-spec entry is definition task N % count (see IBenchmarkFamily.Generate with an index); rng is ignored.
/// Every task records the definition's content hash in TaskHidden.Reference, so an edited definition changes the task hash.
/// </summary>
public sealed class DefinitionFamily : IBenchmarkFamily
{
    private sealed record TaskDef(string? Prompt, string? PromptFile, string? Question, string? System, AnswerFormat AnswerFormat,
        IReadOnlyList<GraderSpec> Graders, string? Reference, IReadOnlyList<string>? KnownBad);

    private sealed record DefFile(string? Id, int Version, string Category, ExecutionMode Mode, int Complexity,
        IReadOnlyList<string>? Include, int? MaxBytes, IReadOnlyList<TaskDef>? Tasks, IReadOnlyList<TaskDef>? Questions, string? Description);

    private readonly List<(string? System, string User, AnswerFormat Format, IReadOnlyList<GraderSpec> Graders, string? Reference, IReadOnlyList<string> KnownBad)> _tasks = [];

    public string Id { get; }
    public int Version { get; }
    public string Category { get; }
    public string Description { get; }
    public ExecutionMode Mode { get; }
    public int DefinedComplexity { get; }
    public string ContentHash { get; }

    private DefinitionFamily(string id, DefFile def, string hash, string description)
    {
        (Id, Version, Category, Mode, DefinedComplexity, ContentHash, Description) = (id, def.Version, def.Category, def.Mode, def.Complexity, hash, description);
    }

    // ---------------------------------------------------------------- loading

    /// <summary>`static/&lt;file&gt;.json`: prompts come from `prompt` or a Markdown `promptFile` beside the JSON.</summary>
    public static DefinitionFamily LoadStatic(string jsonPath)
    {
        var text = File.ReadAllText(jsonPath);
        var def = Parse(jsonPath, text);
        var hash = new StringBuilder(text);
        if (def.Id is null) throw new InvalidDataException($"{jsonPath}: missing id");
        var tasks = def.Tasks ?? throw new InvalidDataException($"{jsonPath}: missing tasks");
        var prompts = new List<string>();
        foreach (var t in tasks)
        {
            string prompt;
            if (t.PromptFile is { } pf)
            {
                var path = Path.Combine(Path.GetDirectoryName(jsonPath)!, pf);
                prompt = File.ReadAllText(path).ReplaceLineEndings("\n").TrimEnd();
                hash.Append('\0').Append(pf).Append('\0').Append(prompt);
            }
            else prompt = t.Prompt ?? throw new InvalidDataException($"{jsonPath}: task needs prompt or promptFile");
            prompts.Add(prompt);
        }
        return Build(jsonPath, def, hash, tasks, prompts, "static:" + def.Id, def.Description ?? $"Static benchmark {def.Id}");
    }

    /// <summary>`repos/&lt;name&gt;/benchmark.json` plus `repo/`: files matching `include` are shown in every prompt, capped at `maxBytes`.</summary>
    public static DefinitionFamily LoadRepo(string jsonPath)
    {
        var dir = Path.GetDirectoryName(jsonPath)!;
        var name = Path.GetFileName(dir);
        var text = File.ReadAllText(jsonPath);
        var def = Parse(jsonPath, text);
        var hash = new StringBuilder(text);
        var include = (def.Include ?? ["**/*"]).Select(Glob).ToList();
        var root = Path.Combine(dir, "repo");
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => include.Any(re => re.IsMatch(f)))
            .Order(StringComparer.Ordinal).ToList();
        var shown = new StringBuilder("The repository consists of the following files.\n");
        var bytes = 0;
        foreach (var f in files)
        {
            var body = File.ReadAllText(Path.Combine(root, f)).ReplaceLineEndings("\n").TrimEnd();
            bytes += Encoding.UTF8.GetByteCount(body);
            if (bytes > (def.MaxBytes ?? 60000)) throw new InvalidDataException($"{jsonPath}: included files exceed maxBytes {def.MaxBytes ?? 60000} at {f}");
            shown.Append($"\n### `{f}`\n```{Path.GetExtension(f).TrimStart('.')}\n{body}\n```\n");
            hash.Append('\0').Append(f).Append('\0').Append(body);
        }
        var qs = def.Questions ?? throw new InvalidDataException($"{jsonPath}: missing questions");
        var prompts = qs.Select(q => $"{shown}\n{q.Question ?? throw new InvalidDataException($"{jsonPath}: question text missing")}").ToList();
        return Build(jsonPath, def, hash, qs, prompts, "repo:" + name, def.Description ?? $"Questions about the {name} repository");
    }

    private static DefinitionFamily Build(string path, DefFile def, StringBuilder hashInput, IReadOnlyList<TaskDef> tasks, List<string> prompts, string id, string description)
    {
        if (tasks.Count == 0) throw new InvalidDataException($"{path}: no tasks");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput.ToString())));
        var fam = new DefinitionFamily(id, def, hash, description);
        for (var i = 0; i < tasks.Count; i++)
            fam._tasks.Add((tasks[i].System, prompts[i], tasks[i].AnswerFormat, tasks[i].Graders, tasks[i].Reference, tasks[i].KnownBad ?? []));
        return fam;
    }

    private static DefFile Parse(string path, string text)
    {
        try { return JsonUtil.Deserialize<DefFile>(text); }
        catch (System.Text.Json.JsonException ex) { throw new InvalidDataException($"{path}: {ex.Message}", ex); }
    }

    private static Regex Glob(string pattern)
    {
        var re = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                var slash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                re.Append(slash ? "(.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else if (pattern[i] == '*') re.Append("[^/]*");
            else if (pattern[i] == '?') re.Append("[^/]");
            else re.Append(Regex.Escape(pattern[i].ToString()));
        }
        return new Regex(re.Append('$').ToString(), RegexOptions.CultureInvariant);
    }

    // ---------------------------------------------------------------- family contract

    public GeneratedTask Generate(Rng rng, int complexity) => Generate(rng, complexity, 0);

    public GeneratedTask Generate(Rng rng, int complexity, int index)
    {
        var at = index % _tasks.Count;
        var t = _tasks[at];
        IReadOnlyList<ChatMessage> Turn(string s) => [new ChatMessage(Role.Assistant, s)];
        return new GeneratedTask(
            new TaskPrompt(t.System, t.User, t.Format, []),
            new TaskHidden(t.Graders, null, JsonUtil.Element(new { contentHash = ContentHash, task = at })),
            t.Reference is null ? [] : Turn(t.Reference),
            t.KnownBad.Select(Turn).ToList());
    }

    public ValidationResult Validate(GeneratedTask task)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(task.Prompt.User)) problems.Add("empty prompt");
        if (task.Hidden.Graders.Count == 0) problems.Add("no graders");
        foreach (var g in task.Hidden.Graders)
            try { GraderRegistry.Get(g.Type); } catch (InvalidOperationException ex) { problems.Add(ex.Message); }
        if (task.Hidden.Reference?.GetProperty("contentHash").GetString() != ContentHash) problems.Add("task was generated from a different definition content hash");
        if (task.ReferenceTurns.Count > 0 && task.KnownBadTurns.Count == 0) problems.Add("a reference answer needs at least one known-bad answer");
        return problems.Count == 0 ? ValidationResult.Valid : new ValidationResult(false, problems);
    }
}

/// <summary>Finds and loads file-defined families. Directory: MODELBENCH_BENCHMARKS, else the nearest `benchmarks` directory above the app.</summary>
public static class DefinitionLoader
{
    public static IReadOnlyList<IBenchmarkFamily> Discover()
    {
        var dir = Environment.GetEnvironmentVariable("MODELBENCH_BENCHMARKS");
        if (string.IsNullOrEmpty(dir))
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null && dir is null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "benchmarks"))) dir = Path.Combine(d.FullName, "benchmarks");
        return dir is not null && Directory.Exists(dir) ? Load(dir) : [];
    }

    public static IReadOnlyList<IBenchmarkFamily> Load(string benchmarksDir)
    {
        var all = new List<IBenchmarkFamily>();
        var statics = Path.Combine(benchmarksDir, "static");
        if (Directory.Exists(statics))
            all.AddRange(Directory.GetFiles(statics, "*.json").Order(StringComparer.Ordinal).Select(DefinitionFamily.LoadStatic));
        var repos = Path.Combine(benchmarksDir, "repos");
        if (Directory.Exists(repos))
            all.AddRange(Directory.GetDirectories(repos).Order(StringComparer.Ordinal)
                .Select(d => Path.Combine(d, "benchmark.json")).Where(File.Exists).Select(DefinitionFamily.LoadRepo));
        return all;
    }
}
