using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelBench.Core.Domain;

namespace ModelBench.Core.Config;


public sealed record CatalogEntry(string Ref, ApiKind Api, bool Reasoning, int? ContextWindow, IReadOnlyList<string> Variants);

/// <summary>models.json loader: a superset of pi's models.json. Unknown fields are ignored.</summary>
public sealed partial class ModelCatalog
{
    private static readonly IReadOnlyDictionary<string, ApiKind> ApiKinds = new Dictionary<string, ApiKind>
    {
        ["openai-completions"] = ApiKind.OpenAiCompletions,
        ["manual"] = ApiKind.Manual,
    };

    private readonly JsonObject _providers;

    private ModelCatalog(JsonObject providers) => _providers = providers;

    [GeneratedRegex(@"\{env:([A-Za-z_][A-Za-z0-9_]*)\}")] private static partial Regex EnvPattern();

    public static ModelCatalog Parse(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new InvalidOperationException("models.json must be a JSON object");
        return new ModelCatalog(root["providers"] as JsonObject ?? throw new InvalidOperationException("models.json has no \"providers\" object"));
    }

    /// <summary>--models path, then MODELBENCH_MODELS, then ./models.json, then ~/.modelbench/models.json. Null when none exists.</summary>
    public static string? Locate(string? explicitPath)
    {
        if (string.IsNullOrWhiteSpace(explicitPath)) explicitPath = Environment.GetEnvironmentVariable("MODELBENCH_MODELS");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : throw new FileNotFoundException($"models file not found: {explicitPath}");
        string[] candidates =
        [
            Path.Combine(Directory.GetCurrentDirectory(), "models.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".modelbench", "models.json"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public static ModelCatalog Load(string? explicitPath)
    {
        var path = Locate(explicitPath)
            ?? throw new FileNotFoundException("no models.json found (looked for --models, MODELBENCH_MODELS, ./models.json, ~/.modelbench/models.json)");
        return Parse(File.ReadAllText(path));
    }

    public IReadOnlyList<CatalogEntry> List()
        => All().Where(x => x.Why is null)
            .Select(x => new CatalogEntry(x.Ref, ParseApi(x.M["api"] ?? x.P["api"], x.Pid), x.M["reasoning"]?.GetValue<bool>() ?? false,
                x.M["contextWindow"]?.GetValue<int>(), VariantMap(x.M).Keys.ToList())).ToList();

    /// <summary>Models <see cref="List"/> leaves out, as "ref (reason)": hosted apis and endpoints that are not local. Shown so typos are visible.</summary>
    public IReadOnlyList<string> Skipped() => All().Where(x => x.Why is not null).Select(x => $"{x.Ref} ({x.Why})").ToList();

    private IEnumerable<(string Ref, string Pid, JsonObject P, JsonObject M, string? Why)> All()
        => _providers.SelectMany(kv => Models(kv.Value!.AsObject(), kv.Key)
            .Select(m => ($"{kv.Key}/{(string)m["id"]!}", kv.Key, kv.Value!.AsObject(), m, NotRunnable(kv.Value!.AsObject(), m))));

    /// <summary>Null when the model is runnable: a supported api and, for openai-completions, a local endpoint (model baseUrl overrides provider's).</summary>
    private static string? NotRunnable(JsonObject p, JsonObject m)
    {
        var api = (string?)(m["api"] ?? p["api"]);
        if (api is null || !ApiKinds.TryGetValue(api, out var kind)) return $"api '{api}' is not supported";
        return kind == ApiKind.OpenAiCompletions ? LocalEndpoint.Check((string?)(m["baseUrl"] ?? p["baseUrl"])) : null;
    }

    /// <summary>"provider/modelId[@variant]": provider = up to the first '/', variant = after the last '@'.</summary>
    public ResolvedModel Resolve(string reference)
    {
        var slash = reference.IndexOf('/');
        if (slash <= 0 || slash == reference.Length - 1) throw new ArgumentException($"'{reference}' is not provider/model[@variant]");
        var providerId = reference[..slash];
        var rest = reference[(slash + 1)..];
        var at = rest.LastIndexOf('@');
        var variant = at >= 0 ? rest[(at + 1)..] : null;
        var modelId = at >= 0 ? rest[..at] : rest;

        var p = _providers[providerId] as JsonObject ?? throw new ArgumentException($"unknown provider '{providerId}'");
        var m = Models(p, providerId).FirstOrDefault(x => (string?)x["id"] == modelId)
            ?? throw new ArgumentException($"provider '{providerId}' has no model '{modelId}'");
        if (NotRunnable(p, m) is { } why) throw new InvalidOperationException($"'{reference}' cannot run: {why}. ModelBench runs local models only (openai-completions on a loopback or private-network address, or manual)");

        var variants = VariantMap(m);
        JsonObject variantSettings = [];
        if (variant is not null)
            variantSettings = (JsonObject)(variants.TryGetValue(variant, out var v) ? v.DeepClone()
                : throw new ArgumentException($"model '{providerId}/{modelId}' has no variant '{variant}' (available: {string.Join(", ", variants.Keys)})"));

        var headers = new Dictionary<string, string>();
        if (p["headers"] is JsonObject h)
            foreach (var (k, val) in h) headers[k] = Substitute((string)val!);
        var cost = m["cost"] as JsonObject;
        var energy = p["energy"] as JsonObject;

        return new ResolvedModel(
            Ref: reference, ProviderId: providerId, Api: ParseApi(m["api"] ?? p["api"], providerId),
            BaseUrl: (string?)(m["baseUrl"] ?? p["baseUrl"]), ModelId: modelId, DisplayName: (string?)m["name"],
            ApiKey: p["apiKey"] is { } key ? Substitute((string)key!) : null,
            Headers: headers,
            Reasoning: m["reasoning"]?.GetValue<bool>() ?? false,
            ToolCalls: m["toolCalls"]?.GetValue<bool>() ?? true,
            ContextWindow: m["contextWindow"]?.GetValue<int>(),
            MaxTokens: m["maxTokens"]?.GetValue<int>(),
            Pricing: cost is null ? null : new Pricing(cost["input"]?.GetValue<decimal>() ?? 0, cost["output"]?.GetValue<decimal>() ?? 0, cost["reasoning"]?.GetValue<decimal>()),
            Energy: energy is null ? null : new EnergyProfile(EnergyField<double>(energy, "watts", reference), EnergyField<decimal>(energy, "pricePerKwh", reference)),
            Variant: variant,
            Sampling: Clone(m["samplingParams"]), VariantSettings: variantSettings, Compat: Clone(m["compat"]), Extra: Clone(m["extra"]));
    }

    /// <summary>JSON for Run.ModelSnapshotJson: no ApiKey, header values replaced by "***".</summary>
    public static string Snapshot(ResolvedModel model)
        => JsonUtil.Serialize(model with { ApiKey = null, Headers = model.Headers.ToDictionary(kv => kv.Key, _ => "***") });

    private static IEnumerable<JsonObject> Models(JsonObject provider, string id)
        => (provider["models"] as JsonArray ?? throw new InvalidOperationException($"provider '{id}' has no models array")).Select(n => n!.AsObject());

    private static T EnergyField<T>(JsonObject energy, string field, string reference)
        => energy[field] is JsonValue v && v.TryGetValue<T>(out var x) ? x
            : throw new InvalidOperationException($"'{reference}': provider energy.{field} is missing or not a number");

    private static JsonObject Clone(JsonNode? n) => n is JsonObject o ? (JsonObject)o.DeepClone() : [];

    /// <summary>pi's thinkingLevelMap becomes {reasoningEffort: value} variants; explicit variants win on name clashes.</summary>
    private static Dictionary<string, JsonObject> VariantMap(JsonObject model)
    {
        var map = new Dictionary<string, JsonObject>();
        if (model["thinkingLevelMap"] is JsonObject levels)
            foreach (var (level, effort) in levels) map[level] = new JsonObject { ["reasoningEffort"] = (string)effort! };
        if (model["variants"] is JsonObject vs)
            foreach (var (name, settings) in vs) map[name] = settings!.AsObject();
        return map;
    }

    private static ApiKind ParseApi(JsonNode? api, string provider)
        => api is not null && ApiKinds.TryGetValue((string)api!, out var k) ? k
            : throw new InvalidOperationException($"provider '{provider}': api '{(string?)api}' is not supported. ModelBench runs local models only: {string.Join(", ", ApiKinds.Keys)}");

    private static string Substitute(string s) => EnvPattern().Replace(s, m =>
        Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? throw new InvalidOperationException($"environment variable {m.Groups[1].Value} is not set"));
}
