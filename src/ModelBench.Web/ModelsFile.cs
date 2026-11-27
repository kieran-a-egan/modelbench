using System.Text.Json;
using System.Text.Json.Nodes;
using ModelBench.Core.Config;

namespace ModelBench.Web;

/// <summary>The models.json the dashboard reads and edits. Literal secrets are masked as "***" in the editor and restored on save.</summary>
public sealed class ModelsFile(string? configuredPath)
{
    public const string Mask = "***";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Configured path (even if the file does not exist yet), else the CLI lookup (./models.json, ~/.modelbench/models.json), else where a new file would be created.</summary>
    public string Path => !string.IsNullOrWhiteSpace(configuredPath) ? System.IO.Path.GetFullPath(configuredPath)
        : ModelCatalog.Locate(null)
        ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".modelbench", "models.json");

    public bool Exists => File.Exists(Path);

    public ModelCatalog Catalog() => ModelCatalog.Parse(Exists ? File.ReadAllText(Path) : "{\"providers\":{}}");

    private static bool Literal(string? s) => !string.IsNullOrEmpty(s) && !s.Contains("{env:");

    /// <summary>File text with literal apiKey and header values replaced by "***".</summary>
    public string MaskedText()
    {
        if (!Exists) return "{\n  \"providers\": {}\n}";
        var root = JsonNode.Parse(File.ReadAllText(Path), documentOptions: Doc) as JsonObject ?? throw new InvalidOperationException("models.json must be a JSON object");
        foreach (var p in Providers(root))
        {
            if (p["apiKey"] is JsonValue k && Literal((string?)k)) p["apiKey"] = Mask;
            if (p["headers"] is JsonObject h)
                foreach (var key in h.Select(kv => kv.Key).ToList())
                    if (Literal((string?)h[key])) h[key] = Mask;
        }
        return root.ToJsonString(Indented);
    }

    /// <summary>Restores masked values from the current file, validates via ModelCatalog, then writes. Returns an error message or null.</summary>
    public string? Save(string text)
    {
        try
        {
            var root = JsonNode.Parse(text, documentOptions: Doc) as JsonObject ?? throw new InvalidOperationException("models.json must be a JSON object");
            var original = Exists ? JsonNode.Parse(File.ReadAllText(Path), documentOptions: Doc) as JsonObject : null;
            foreach (var (id, node) in root["providers"] as JsonObject ?? [])
            {
                if (node is not JsonObject p) continue;
                var orig = original?["providers"]?[id] as JsonObject;
                if ((string?)p["apiKey"] == Mask) p["apiKey"] = Original(orig?["apiKey"], $"provider '{id}' apiKey");
                if (p["headers"] is JsonObject h)
                    foreach (var key in h.Select(kv => kv.Key).ToList())
                        if ((string?)h[key] == Mask) h[key] = Original((orig?["headers"] as JsonObject)?[key], $"provider '{id}' header '{key}'");
            }
            var json = root.ToJsonString(Indented);
            var catalog = ModelCatalog.Parse(json);
            foreach (var e in catalog.List())
                try { catalog.Resolve(e.Ref); } // validates providers, models and energy; non-local apis are skipped and shown on the page
                catch (MissingEnvironmentVariableException) { } // the file is valid; the variable is the host's business, so it must not block saving other edits
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, json);
            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or InvalidCastException or FormatException or IOException or ArgumentException)
        {
            return ex.Message;
        }
    }

    private static readonly JsonDocumentOptions Doc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static IEnumerable<JsonObject> Providers(JsonObject root) => (root["providers"] as JsonObject ?? []).Select(kv => kv.Value).OfType<JsonObject>();

    private static JsonNode Original(JsonNode? orig, string what)
        => orig is JsonValue v && (string?)v is { } s ? JsonValue.Create(s)! : throw new InvalidOperationException($"{what} is \"{Mask}\" but the file has no original value to keep");
}
