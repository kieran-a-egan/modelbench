using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelBench.Core.Domain;
using ModelBench.Core.Generation;
using ModelBench.Core.Grading;

namespace ModelBench.Core.Families;

/// <summary>Reference.Changes is the number of required state changes (tickets changed plus files created or modified).</summary>
public sealed record WsRef(string Kind, string? Assignee, string? OldKey, string? NewKey, int Changes);

/// <summary>
/// Agentic goals over a <see cref="WorkspaceEnvironment"/>: archive tickets whose log has ERROR and move the logs, rename a config key
/// across /config, or close the tickets of one assignee whose referenced file is missing. Complexity sets the number of required
/// state changes (1-2, 2-3, 3-5, 4-7, 6-10) and the number of look-alike distractors (complexity + 1).
/// The initial state lives only in TaskHidden.Environment; the prompt holds the goal and the tool list.
/// </summary>
public sealed class AgenticTaskCompletionFamily : IBenchmarkFamily
{
    public string Id => "agentic-task-completion";
    public int Version => 1;
    public string Category => "agentic-task-completion";
    public string Description => "Reach a goal state in a simulated file tree and ticket tracker with 1-10 required changes and look-alike distractors.";
    public ExecutionMode Mode => ExecutionMode.Agentic;

    private static readonly int[] Lo = [1, 2, 3, 4, 6], Hi = [2, 3, 5, 7, 10];
    private static readonly string[] Kinds = ["archive", "rename", "close"];
    private static readonly string[] Words = ["auth", "billing", "search", "gateway", "mailer", "cache", "ingest", "report", "sched", "export", "notify", "audit",
        "queue", "render", "sync", "upload", "vault", "metrics", "proxy", "index", "session", "orders", "catalog", "pricing"];
    private static readonly string[] Names = ["maren", "joel", "elke", "kasim", "rina", "bodil", "lev", "dara"];
    private static readonly string[] Titles = ["Investigate nightly failures", "Check slow responses", "Review alert noise", "Verify rollout", "Follow up on customer report", "Audit retention", "Tune thresholds", "Confirm backup"];
    private static readonly string[] Levels = ["INFO", "DEBUG", "WARN"];
    private static readonly string[] Messages = ["service started", "cache warmed", "retry after transient error", "request handled", "config reloaded", "connection pool resized"];
    private static readonly string[] ErrorMessages = ["unhandled exception in worker", "database connection refused", "timeout talking to upstream", "failed to parse payload"];
    private static readonly (string Old, string New)[] KeyPairs = [("max_conn", "max_connections"), ("timeout", "timeout_seconds"), ("log_lvl", "log_level"), ("retries", "retry_limit"), ("host", "hostname")];
    private static readonly string[] OtherKeys = ["port", "mode", "region", "workers", "user", "cache_ttl"];

    private const string SystemPrompt = "You are an operations assistant working inside a virtual workspace (a file tree and a ticket tracker) that you can only reach through the provided tools. "
        + "Carry out the goal by calling tools. When it is complete, reply with one short sentence saying what you did.";

    private static readonly IReadOnlyList<ToolDefinition> Tools =
    [
        T("list_dir", "List the entries of a directory (subdirectories end with '/').", """{"path":{"type":"string","description":"Absolute directory path, e.g. /logs"}}""", "path"),
        T("read_file", "Read a text file.", """{"path":{"type":"string","description":"Absolute file path"}}""", "path"),
        T("write_file", "Create or overwrite a text file (max 8 KB).", """{"path":{"type":"string","description":"Absolute file path"},"content":{"type":"string","description":"Full new content"}}""", "path", "content"),
        T("move_file", "Move or rename a file. The destination must not exist.", """{"from":{"type":"string","description":"Existing file path"},"to":{"type":"string","description":"New file path"}}""", "from", "to"),
        T("delete_file", "Permanently delete a file.", """{"path":{"type":"string","description":"Absolute file path"}}""", "path"),
        T("search", "Find files whose content contains the query text (case-sensitive substring). Returns path, line number and line.", """{"query":{"type":"string","description":"Text to look for"}}""", "query"),
        T("list_tickets", "List tickets (id, title, status, assignee, file, comment). Statuses: open, in_progress, closed, archived.", """{"status":{"type":"string","description":"Only tickets with this status"}}"""),
        T("update_ticket", "Change a ticket's status, assignee and/or comment.", """{"id":{"type":"string","description":"Ticket id, e.g. T-123"},"status":{"type":"string","description":"open, in_progress, closed or archived"},"assignee":{"type":"string","description":"New assignee"},"comment":{"type":"string","description":"New comment (replaces the old one)"}}""", "id"),
    ];

    private static ToolDefinition T(string name, string description, string properties, params string[] required) => ToolCallingFamily.Tool(name, description, properties, required);

    public GeneratedTask Generate(Rng rng, int c)
    {
        if (c is < Complexity.Min or > Complexity.Max) throw new ArgumentOutOfRangeException(nameof(c));
        var n = rng.Range(Lo[c - 1], Hi[c - 1]);
        var kind = rng.Pick(Kinds);
        var d = c + 1;
        var words = new List<string>(Words);
        rng.Shuffle(words);
        var w = 0;
        string Word() => words[w++];
        var next = rng.Range(100, 400);
        string NextId() { next += rng.Range(1, 6); return $"T-{next}"; }
        List<bool> Flags(int hits, int total) { var f = Enumerable.Range(0, total).Select(i => i < hits).ToList(); rng.Shuffle(f); return f; }

        var files = new List<WsFile>();
        var tickets = new List<WsTicket>();
        var expFiles = new JsonObject();
        var expTickets = new JsonObject();
        var calls = new List<ToolCall>();
        void Call(string name, object args) => calls.Add(new ToolCall($"call_{calls.Count + 1}", name, JsonUtil.Serialize(args)));
        string goal, assignee = "", oldKey = "", newKey = "", distractorTicket = "", outsidePath = "";
        string[] forbidden;
        int changes;

        switch (kind)
        {
            case "archive":
            {
                var t = (n + 1) / 2;
                changes = 2 * t;
                goal = "Archive every ticket whose referenced log file contains the text ERROR (uppercase): set its status to 'archived'. Then move each of those log files into /archive, keeping the file name.";
                forbidden = ["delete_file"];
                Call("list_tickets", new { });
                Call("search", new { query = "ERROR" });
                foreach (var bad in Flags(t, t + d))
                {
                    var name = Word();
                    var path = $"/logs/{name}.log";
                    var content = Log(rng, bad);
                    files.Add(new WsFile(path, content));
                    var id = NextId();
                    tickets.Add(new WsTicket(id, rng.Pick(Titles), "open", rng.Pick(Names), path, null));
                    if (bad)
                    {
                        Call("update_ticket", new { id, status = "archived" });
                        Call("move_file", new { from = path, to = $"/archive/{name}.log" });
                        expTickets[id] = new JsonObject { ["status"] = "archived" };
                        expFiles[path] = null;
                        expFiles[$"/archive/{name}.log"] = content;
                    }
                    else
                    {
                        distractorTicket = id;
                        expTickets[id] = new JsonObject { ["status"] = "open" };
                        expFiles[path] = content;
                    }
                }
                break;
            }
            case "rename":
            {
                changes = n;
                (oldKey, newKey) = rng.Pick(KeyPairs);
                goal = $"In every file directly under /config, rename the config key '{oldKey}' to '{newKey}'. Only change the key before '='; keep every value and every other line exactly as it is. Do not touch files outside /config.";
                forbidden = ["delete_file", "move_file"];
                // One body builder renders both versions, so only the key differs between old and new text.
                (string Old, string New) Body(string name, bool hasKey)
                {
                    var keys = new List<string>(OtherKeys);
                    rng.Shuffle(keys);
                    keys = keys.Take(3).ToList();
                    if (hasKey) keys.Insert(rng.Range(0, keys.Count), oldKey);
                    var vals = keys.Select(_ => rng.Range(1, 999)).ToArray();
                    string Render(Func<string, string> key) => new StringBuilder().Ln($"# {name} settings").Append(string.Concat(keys.Select((k, i) => $"{key(k)}={vals[i]}\n"))).ToString();
                    return (Render(k => k), Render(k => k == oldKey ? newKey : k));
                }
                Call("search", new { query = oldKey + "=" });
                foreach (var affected in Flags(n, n + d))
                {
                    var name = Word();
                    var path = $"/config/{name}.cfg";
                    var (oldText, newText) = Body(name, affected);
                    files.Add(new WsFile(path, oldText));
                    expFiles[path] = affected ? newText : oldText;
                    if (!affected) continue;
                    Call("read_file", new { path });
                    Call("write_file", new { path, content = newText });
                }
                outsidePath = "/backup/legacy.cfg";
                var legacy = Body("legacy", true).Old;
                files.Add(new WsFile(outsidePath, legacy));
                expFiles[outsidePath] = legacy;
                break;
            }
            default:
            {
                changes = n;
                assignee = rng.Pick(Names);
                var other = rng.Pick(Names.Where(x => x != assignee).ToList());
                var da = Math.Max(1, d / 2);
                goal = $"Close every ticket assigned to {assignee} whose referenced file does not exist: set its status to 'closed' and its comment to 'missing'. Leave all other tickets unchanged.";
                forbidden = ["delete_file", "move_file", "write_file"];
                Call("list_tickets", new { });
                Call("list_dir", new { path = "/docs" });
                var tags = Enumerable.Range(0, n + d).Select(i => i < n ? 'm' : i < n + da ? 'a' : 'b').ToList();
                rng.Shuffle(tags);
                foreach (var tag in tags)
                {
                    var path = $"/docs/{Word()}.md";
                    if (tag == 'a')
                    {
                        files.Add(new WsFile(path, $"Notes for {path}\n"));
                        expFiles[path] = $"Notes for {path}\n";
                    }
                    var id = NextId();
                    tickets.Add(new WsTicket(id, rng.Pick(Titles), "open", tag == 'b' ? other : assignee, path, null));
                    if (tag == 'm')
                    {
                        Call("update_ticket", new { id, status = "closed", comment = "missing" });
                        expTickets[id] = new JsonObject { ["status"] = "closed", ["comment"] = "missing" };
                    }
                    else
                    {
                        if (tag == 'a' && distractorTicket == "") distractorTicket = id;
                        expTickets[id] = new JsonObject { ["status"] = "open", ["comment"] = null };
                    }
                }
                break;
            }
        }

        var expected = new JsonObject { ["files"] = expFiles };
        if (expTickets.Count > 0) expected["tickets"] = expTickets;
        var hidden = new TaskHidden(
        [
            new GraderSpec("env-state", "env-state", JsonUtil.Element(new { expected = JsonDocument.Parse(expected.ToJsonString()).RootElement.Clone() })),
            new GraderSpec("tool-calls", "tool-calls", JsonUtil.Element(new { forbidden, maxCalls = 2 * calls.Count + 10 })),
        ], JsonUtil.Element(new WsData(files, tickets)), JsonUtil.Element(new WsRef(kind, kind == "close" ? assignee : null, kind == "rename" ? oldKey : null, kind == "rename" ? newKey : null, changes)));
        var prompt = new TaskPrompt(SystemPrompt,
            $"Goal: {goal}\n\nPaths are absolute and start with '/'. A ticket has an id, title, status, assignee, file (the path of the file it refers to) and comment.", AnswerFormat.FreeText, Tools);

        const string Done = "All requested changes are done.";
        List<ChatMessage> Turns(IEnumerable<ToolCall> cs) => [.. cs.Select(x => new ChatMessage(Role.Assistant, null, [x])), new ChatMessage(Role.Assistant, Done)];
        static bool Mutates(ToolCall x) => x.Name is "update_ticket" or "move_file" or "write_file";
        static ToolCall Delete(string path) => new("call_x", "delete_file", JsonUtil.Serialize(new { path }));
        ToolCall Wrong(ToolCall x)
        {
            var o = JsonNode.Parse(x.ArgumentsJson)!.AsObject();
            return x.Name switch
            {
                "move_file" => Delete((string)o["from"]!),
                "write_file" => Delete((string)o["path"]!),
                "update_ticket" when kind == "close" => x with { ArgumentsJson = JsonUtil.Serialize(new { id = (string)o["id"]!, status = "archived" }) },
                _ => x,
            };
        }
        ToolCall Retarget(ToolCall x)
        {
            var o = JsonNode.Parse(x.ArgumentsJson)!.AsObject();
            if (x.Name == "update_ticket") o["id"] = distractorTicket; else o["path"] = outsidePath;
            return x with { ArgumentsJson = o.ToJsonString() };
        }
        var firstMutation = calls.FindIndex(Mutates);
        return new GeneratedTask(prompt, hidden, Turns(calls),
        [
            Turns(calls.Take(calls.FindLastIndex(Mutates))),                                          // stops before the last change
            Turns(calls.Select(Wrong)),                                                                // deletes (or archives) instead of the asked change
            Turns(calls.Select((x, i) => i == firstMutation ? Retarget(x) : x)),                     // first change lands on the wrong target
        ]);
    }

    private static string Log(Rng rng, bool error)
    {
        var sb = new StringBuilder();
        var lines = rng.Range(4, 6);
        var at = error ? rng.Next(lines) : -1;
        for (var i = 0; i < lines; i++)
        {
            var stamp = $"2024-05-{rng.Range(10, 28)} 0{rng.Range(1, 9)}:{rng.Range(10, 59)}:{rng.Range(10, 59)}";
            sb.Ln(i == at ? $"{stamp} ERROR {rng.Pick(ErrorMessages)}" : $"{stamp} {rng.Pick(Levels)} {rng.Pick(Messages)}");
        }
        return sb.ToString();
    }

    public ValidationResult Validate(GeneratedTask task)
    {
        var h = task.Hidden;
        if (h.Environment is not { } envJson || h.Reference is not { } refJson) return ValidationResult.Fail("missing environment or reference");
        var data = JsonUtil.Deserialize<WsData>(envJson.GetRawText());
        var r = JsonUtil.Deserialize<WsRef>(refJson.GetRawText());
        var problems = new List<string>();

        // Independent expected final state: a direct transformation of the initial state, never going through the tools.
        var initial = data.Files.ToDictionary(f => f.Path, f => f.Content);
        var files = new Dictionary<string, string>(initial);
        var initialTickets = data.Tickets.ToDictionary(t => t.Id);
        var tickets = new Dictionary<string, WsTicket>(initialTickets);
        switch (r.Kind)
        {
            case "archive":
                foreach (var t in data.Tickets.Where(t => t.File is not null && files.TryGetValue(t.File, out var body) && body.Contains("ERROR", StringComparison.Ordinal)).ToList())
                {
                    tickets[t.Id] = t with { Status = "archived" };
                    var file = t.File!;
                    files["/archive/" + file[(file.LastIndexOf('/') + 1)..]] = files[file];
                    files.Remove(file);
                }
                break;
            case "rename":
                foreach (var (path, body) in files.Where(kv => kv.Key.StartsWith("/config/", StringComparison.Ordinal) && kv.Key.IndexOf('/', 8) < 0).ToList())
                    files[path] = body.Replace("\n" + r.OldKey + "=", "\n" + r.NewKey + "=");
                break;
            case "close":
                foreach (var t in data.Tickets.Where(t => t.Assignee == r.Assignee && !files.ContainsKey(t.File!)))
                    tickets[t.Id] = t with { Status = "closed", Comment = "missing" };
                break;
            default: return ValidationResult.Fail($"unknown kind '{r.Kind}'");
        }
        var direct = JsonUtil.Element(new { files = new SortedDictionary<string, string>(files, StringComparer.Ordinal), tickets = new SortedDictionary<string, WsTicket>(tickets, StringComparer.Ordinal) });

        var envSpec = h.Graders.FirstOrDefault(g => g.Type == "env-state");
        var toolSpec = h.Graders.FirstOrDefault(g => g.Type == "tool-calls");
        if (envSpec is null || toolSpec is null) return ValidationResult.Fail("missing env-state or tool-calls grader");
        var expected = envSpec.Config.GetProperty("expected");
        foreach (var o in EnvStateGrader.Evaluate(expected, direct).Where(o => !o.Passed)) problems.Add($"hidden expectation disagrees with the direct transformation: {o.Diagnostic}");

        // Every changed file and ticket must be asserted, and the change count must match.
        var changedFiles = files.Keys.Union(initial.Keys).Where(k => !(initial.TryGetValue(k, out var a) && files.TryGetValue(k, out var b) && a == b)).ToList();
        var changedTickets = tickets.Values.Where(t => initialTickets[t.Id] != t).ToList();
        expected.TryGetProperty("files", out var ef);
        expected.TryGetProperty("tickets", out var et);
        foreach (var k in changedFiles) if (ef.ValueKind != JsonValueKind.Object || !ef.TryGetProperty(k, out _)) problems.Add($"changed file {k} is not asserted");
        foreach (var t in changedTickets) if (et.ValueKind != JsonValueKind.Object || !et.TryGetProperty(t.Id, out _)) problems.Add($"changed ticket {t.Id} is not asserted");
        var count = changedTickets.Count + files.Count(kv => !initial.TryGetValue(kv.Key, out var o) || o != kv.Value);
        if (count != r.Changes || count == 0) problems.Add($"{count} state changes differ from the recorded {r.Changes}");
        if (!toolSpec.Config.TryGetProperty("forbidden", out var forb) || !forb.EnumerateArray().Any(x => x.GetString() == "delete_file")) problems.Add("delete_file is not forbidden");

        // Achievability: the reference turns replayed through a fresh environment reach exactly the direct state.
        var env = new WorkspaceEnvironment(data);
        var refCalls = task.ReferenceTurns.SelectMany(t => t.ToolCalls ?? []).ToList();
        foreach (var call in refCalls)
            if (env.Execute(call).StartsWith("{\"error\"", StringComparison.Ordinal)) problems.Add($"reference call {call.Name} returned an error");
        if (env.State.GetRawText() != direct.GetRawText()) problems.Add("replaying the reference turns does not reach the expected state");
        if (toolSpec.Config.GetProperty("maxCalls").GetInt32() < refCalls.Count) problems.Add("maxCalls is below the reference length");
        if (string.IsNullOrWhiteSpace(task.ReferenceTurns[^1].Content) || task.ReferenceTurns[^1].ToolCalls is { Count: > 0 }) problems.Add("reference does not end with a text turn");

        // Hidden data must not leak into the prompt, and the tool set is fixed.
        var shown = JsonUtil.Serialize(task.Prompt with { Tools = [] });
        foreach (var f in data.Files) if (shown.Contains(JsonUtil.Serialize(f.Content)[1..^1], StringComparison.Ordinal)) problems.Add($"file content of {f.Path} appears in the prompt");
        foreach (var t in data.Tickets) if (shown.Contains(t.Id, StringComparison.Ordinal)) problems.Add($"ticket id {t.Id} appears in the prompt");
        if (!task.Prompt.Tools.Select(t => t.Name).SequenceEqual(Tools.Select(t => t.Name))) problems.Add("unexpected tool set");
        if (task.KnownBadTurns.Count < 3) problems.Add("expected at least 3 known-bad turns");
        return problems.Count == 0 ? ValidationResult.Valid : new ValidationResult(false, problems);
    }

    public ISimulatedEnvironment? CreateEnvironment(TaskHidden hidden)
        => new WorkspaceEnvironment(JsonUtil.Deserialize<WsData>(hidden.Environment!.Value.GetRawText()));
}
