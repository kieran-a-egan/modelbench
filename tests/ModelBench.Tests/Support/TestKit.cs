using System.Text.Json.Nodes;
using ModelBench.Core.Config;
using ModelBench.Core.Domain;
using ModelBench.Core.Generation;
using ModelBench.Core.Persistence;

namespace ModelBench.Tests.Support;


public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("mbtest").FullName;
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public BenchDb OpenDb() => BenchDb.Open(File("test.db"));
    public void Dispose() => Directory.Delete(Path, true);
}

public static class TestKit
{
    /// <summary>models.json with one openai-completions provider "fake" pointing at the fake server.</summary>
    public static string ModelsJson(string baseUrl, string apiKey = "none") => $$$$"""
        {"providers":{"fake":{"api":"openai-completions","baseUrl":"{{{{baseUrl}}}}","apiKey":"{{{{apiKey}}}}",
          "energy":{"watts":300,"pricePerKwh":0.5},
          "models":[{"id":"m1","reasoning":true,"maxTokens":512,"cost":{"input":1,"output":2},
            "samplingParams":{"temperature":0.7},
            "compat":{"maxTokensField":"max_tokens","supportsReasoningEffort":true},
            "variants":{"low":{"reasoningEffort":"low"}}}]},
          "manual":{"api":"manual","models":[{"id":"chat-ui"}]}}}
        """;

    public static ResolvedModel Model(string baseUrl, string reference = "fake/m1@low", string apiKey = "none")
        => ModelCatalog.Parse(ModelsJson(baseUrl, apiKey)).Resolve(reference);

    public static Suite MakeSuite(BenchDb db, int count, int complexity = 2, long seed = 99)
    {
        var suite = SuiteGenerator.Generate(new SuiteSpec("t", seed, [new FamilySpec("mathematics", complexity, count)]));
        SuiteGenerator.Persist(db, suite);
        return suite;
    }

    public static string Expected(TaskInstance t)
        => JsonUtil.Deserialize<TaskHidden>(t.HiddenJson).Graders.First(g => g.Type == "numeric").Config.GetProperty("expected").GetString()!;

    public static string UserText(JsonObject body)
        => body["messages"]!.AsArray().Last(m => (string?)m!["role"] == "user")!["content"]!.GetValue<string>();
}
