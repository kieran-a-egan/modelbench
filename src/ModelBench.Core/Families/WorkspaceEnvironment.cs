using System.Text;
using System.Text.Json;
using ModelBench.Core.Domain;

namespace ModelBench.Core.Families;

public sealed record WsFile(string Path, string Content);
public sealed record WsTicket(string Id, string Title, string Status, string? Assignee, string? File, string? Comment);

/// <summary>Initial state of the workspace (TaskHidden.Environment of agentic-task-completion).</summary>
public sealed record WsData(IReadOnlyList<WsFile> Files, IReadOnlyList<WsTicket> Tickets);

/// <summary>
/// In-memory file tree plus ticket tracker. Nothing touches the real filesystem. Paths are normalised and confined to the
/// virtual root "/": backslashes, control characters and '..' escapes are rejected with error payloads, as are oversize
/// content, too many files and over-long or over-deep paths. Directories are implicit (derived from file paths).
/// State is canonical: { files: { path: content } sorted by path, tickets: { id: ticket } sorted by id } (ordinal).
/// </summary>
public sealed class WorkspaceEnvironment : ISimulatedEnvironment
{
    public const int MaxFileBytes = 8192, MaxFiles = 64, MaxPathLength = 200, MaxPathDepth = 12, MaxSearchResults = 50;
    public static readonly string[] Statuses = ["open", "in_progress", "closed", "archived"];

    private readonly SortedDictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, WsTicket> _tickets = new(StringComparer.Ordinal);

    public WorkspaceEnvironment(WsData data)
    {
        foreach (var f in data.Files) _files[f.Path] = f.Content;
        foreach (var t in data.Tickets) _tickets[t.Id] = t;
    }

    public JsonElement State => JsonUtil.Element(new { files = _files, tickets = _tickets });

    private sealed class ToolError(string message) : Exception(message);

    public string Execute(ToolCall call)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ToolError("arguments must be a JSON object");
            return JsonUtil.Serialize(Run(call.Name, doc.RootElement));
        }
        catch (JsonException) { return Error("arguments are not valid JSON"); }
        catch (ToolError e) { return Error(e.Message); }
    }

    private static string Error(string message) => JsonUtil.Serialize(new { error = message });

    private object Run(string tool, JsonElement a)
    {
        switch (tool)
        {
            case "list_dir":
            {
                var dir = Normalise(Str(a, "path"));
                var prefix = dir == "/" ? "/" : dir + "/";
                var entries = _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(k => { var rest = k[prefix.Length..]; var i = rest.IndexOf('/'); return i < 0 ? rest : rest[..(i + 1)]; })
                    .Distinct().ToList();
                if (entries.Count == 0 && dir != "/") throw new ToolError($"no such directory '{dir}'");
                return new { path = dir, entries };
            }
            case "read_file":
            {
                var p = FilePath(a, "path");
                return _files.TryGetValue(p, out var content) ? new { path = p, content } : throw new ToolError($"no such file '{p}'");
            }
            case "write_file":
            {
                var p = FilePath(a, "path");
                var content = Str(a, "content");
                if (Encoding.UTF8.GetByteCount(content) > MaxFileBytes) throw new ToolError($"content exceeds {MaxFileBytes} bytes");
                if (!_files.ContainsKey(p))
                {
                    if (_files.Count >= MaxFiles) throw new ToolError($"workspace is full ({MaxFiles} files)");
                    CheckFree(p);
                }
                _files[p] = content;
                return new { ok = true, path = p };
            }
            case "move_file":
            {
                var from = FilePath(a, "from");
                var to = FilePath(a, "to");
                if (!_files.TryGetValue(from, out var content)) throw new ToolError($"no such file '{from}'");
                if (from == to) throw new ToolError("source and destination are the same");
                if (_files.ContainsKey(to)) throw new ToolError($"destination '{to}' already exists");
                CheckFree(to);
                _files.Remove(from);
                _files[to] = content;
                return new { ok = true, from, to };
            }
            case "delete_file":
            {
                var p = FilePath(a, "path");
                return _files.Remove(p) ? new { ok = true, path = p } : throw new ToolError($"no such file '{p}'");
            }
            case "search":
            {
                var q = Str(a, "query");
                if (q.Length is 0 or > 200) throw new ToolError("query must be 1-200 characters");
                var matches = _files.Select(f => (f.Key, Lines: f.Value.Split('\n')))
                    .Select(f => (f.Key, Line: Array.FindIndex(f.Lines, l => l.Contains(q, StringComparison.Ordinal)), f.Lines))
                    .Where(f => f.Line >= 0).Take(MaxSearchResults)
                    .Select(f => new { path = f.Key, line = f.Line + 1, text = f.Lines[f.Line] }).ToList();
                return new { matches };
            }
            case "list_tickets":
            {
                var status = Opt(a, "status");
                if (status is not null && !Statuses.Contains(status)) throw new ToolError($"unknown status '{status}'");
                return new { tickets = _tickets.Values.Where(t => status is null || t.Status == status).ToList() };
            }
            case "update_ticket":
            {
                var id = Str(a, "id");
                if (!_tickets.TryGetValue(id, out var t)) throw new ToolError($"unknown ticket '{id}'");
                string? status = Opt(a, "status"), assignee = Opt(a, "assignee"), comment = Opt(a, "comment");
                if (status is null && assignee is null && comment is null) throw new ToolError("provide at least one of status, assignee, comment");
                if (status is not null && !Statuses.Contains(status)) throw new ToolError($"unknown status '{status}'");
                if (assignee?.Length > 40 || comment?.Length > 200) throw new ToolError("assignee or comment too long");
                return _tickets[id] = t with { Status = status ?? t.Status, Assignee = assignee ?? t.Assignee, Comment = comment ?? t.Comment };
            }
            default:
                throw new ToolError($"unknown tool '{tool}'");
        }
    }

    private static string Str(JsonElement a, string name)
        => a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new ToolError($"missing or non-string argument '{name}'");

    private static string? Opt(JsonElement a, string name) => a.TryGetProperty(name, out _) ? Str(a, name) : null;

    private static string FilePath(JsonElement a, string name)
        => Normalise(Str(a, name)) is var p && p != "/" ? p : throw new ToolError("the root is not a file");

    /// <summary>A new file may not sit under an existing file or have files beneath it.</summary>
    private void CheckFree(string p)
    {
        if (_files.Keys.Any(k => k.StartsWith(p + "/", StringComparison.Ordinal) || p.StartsWith(k + "/", StringComparison.Ordinal)))
            throw new ToolError($"'{p}' conflicts with an existing file or directory");
    }

    /// <summary>Resolves '.', '..' and repeated slashes against the virtual root; relative paths are rooted at "/".</summary>
    public static string Normalise(string raw)
    {
        if (raw.Length is 0 or > MaxPathLength || raw.Contains('\\') || raw.Any(char.IsControl)) throw new ToolError("invalid path");
        var stack = new List<string>();
        foreach (var seg in raw.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg != "..") stack.Add(seg);
            else if (stack.Count == 0) throw new ToolError("path escapes the workspace root");
            else stack.RemoveAt(stack.Count - 1);
        }
        if (stack.Count > MaxPathDepth) throw new ToolError($"path deeper than {MaxPathDepth} levels");
        return "/" + string.Join('/', stack);
    }
}
