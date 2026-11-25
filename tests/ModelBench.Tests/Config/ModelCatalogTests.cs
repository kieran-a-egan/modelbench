using ModelBench.Core.Config;

namespace ModelBench.Tests.Config;

public class ModelCatalogTests
{
    private const string PiFile = """{"$schema":"https://pi.dev/schemas/models.schema.json","providers":{"strata":{"name":"Strata Desktop","baseUrl":"http://127.0.0.1:47831/v1","apiKey":"none","api":"openai-completions","models":[{"id":"inkling-2-mini-sq-15","name":"Inkling 2 Mini SQ-15","reasoning":true,"input":["text"],"contextWindow":262144,"maxTokens":16384,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"samplingParams":{"temperature":1.0},"thinkingLevelMap":{"off":"none","minimal":"minimal","low":"low","medium":"medium","high":"high","xhigh":"xhigh"},"compat":{"supportsStore":false,"supportsDeveloperRole":false,"supportsReasoningEffort":true,"thinkingFormat":"openai","maxTokensField":"max_tokens","supportsStrictMode":false}}]}}}""";

    [Fact] public void RealPiFileLoads()
    {
        var m = ModelCatalog.Parse(PiFile).Resolve("strata/inkling-2-mini-sq-15");
        Assert.Equal(ApiKind.OpenAiCompletions, m.Api);
        Assert.Equal("http://127.0.0.1:47831/v1", m.BaseUrl);
        Assert.Equal("inkling-2-mini-sq-15", m.ModelId);
        Assert.Equal("Inkling 2 Mini SQ-15", m.DisplayName);
        Assert.True(m.Reasoning);
        Assert.True(m.ToolCalls);
        Assert.Equal(262144, m.ContextWindow);
        Assert.Equal(16384, m.MaxTokens);
        Assert.Equal(new Pricing(0, 0), m.Pricing);
        Assert.Equal(1.0, m.Sampling["temperature"]!.GetValue<double>());
        Assert.Equal("max_tokens", m.Compat["maxTokensField"]!.GetValue<string>());
        Assert.Null(m.Variant);
        Assert.Empty(m.VariantSettings);
    }

    [Fact] public void ThinkingLevelMapBecomesReasoningEffortVariants()
    {
        var cat = ModelCatalog.Parse(PiFile);
        Assert.Equal("none", cat.Resolve("strata/inkling-2-mini-sq-15@off").VariantSettings["reasoningEffort"]!.GetValue<string>());
        Assert.Equal("xhigh", cat.Resolve("strata/inkling-2-mini-sq-15@xhigh").VariantSettings["reasoningEffort"]!.GetValue<string>());
        Assert.Equal(6, cat.List().Single().Variants.Count);
    }

    [Fact] public void ExampleFileLoadsAndResolvesEveryModel()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "models.example.json"))) dir = dir.Parent ?? throw new FileNotFoundException("models.example.json");
        foreach (var v in new[] { "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "GEMINI_API_KEY" }) Environment.SetEnvironmentVariable(v, "test-key");
        var cat = ModelCatalog.Parse(File.ReadAllText(Path.Combine(dir.FullName, "models.example.json")));
        Assert.Equal(5, cat.List().Count);
        var strata = cat.Resolve("strata/inkling-2-mini-sq-15@high");
        Assert.Equal("high", strata.VariantSettings["reasoningEffort"]!.GetValue<string>());
        Assert.Equal(new EnergyProfile(350, 0.30m), strata.Energy);
        Assert.Equal(ApiKind.Manual, cat.Resolve("manual/chat-ui").Api);
        Assert.Equal(8192, cat.Resolve("anthropic/claude-sonnet-5-5@think8k").VariantSettings["thinkingBudget"]!.GetValue<int>());
        Assert.Equal(new Pricing(1.25m, 10.0m), cat.Resolve("openai/gpt-5.2").Pricing);
    }

    [Fact] public void EnvSubstitutionInKeysAndHeaders()
    {
        Environment.SetEnvironmentVariable("MB_TEST_SECRET_A", "sk-secret-value");
        var cat = ModelCatalog.Parse("""{"providers":{"p":{"api":"openai-completions","baseUrl":"http://x","apiKey":"{env:MB_TEST_SECRET_A}","headers":{"X-Org":"org-{env:MB_TEST_SECRET_A}"},"models":[{"id":"m"}]}}}""");
        var m = cat.Resolve("p/m");
        Assert.Equal("sk-secret-value", m.ApiKey);
        Assert.Equal("org-sk-secret-value", m.Headers["X-Org"]);
        Assert.Null(m.Pricing);
    }

    [Fact] public void MissingEnvVarIsAnError()
    {
        var cat = ModelCatalog.Parse("""{"providers":{"p":{"api":"manual","apiKey":"{env:MB_TEST_DEFINITELY_UNSET}","models":[{"id":"m"}]}}}""");
        Assert.Throws<InvalidOperationException>(() => cat.Resolve("p/m"));
    }

    [Fact] public void SnapshotHasNoSecrets()
    {
        Environment.SetEnvironmentVariable("MB_TEST_SECRET_B", "sk-very-secret");
        var m = ModelCatalog.Parse("""{"providers":{"p":{"api":"openai-completions","baseUrl":"http://x","apiKey":"{env:MB_TEST_SECRET_B}","headers":{"Authorization":"Bearer {env:MB_TEST_SECRET_B}"},"models":[{"id":"m","maxTokens":10}]}}}""").Resolve("p/m");
        var snap = ModelCatalog.Snapshot(m);
        Assert.DoesNotContain("sk-very-secret", snap);
        Assert.DoesNotContain("apiKey", snap, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"Authorization\":\"***\"", snap);
        Assert.Contains("\"maxTokens\":10", snap);
    }

    [Fact] public void RefParsingHandlesColonsAndSlashesInModelIds()
    {
        var cat = ModelCatalog.Parse("""
            {"providers":{"ollama":{"api":"openai-completions","baseUrl":"http://localhost:11434/v1","models":[
              {"id":"qwen3:4b"},
              {"id":"meta/llama","variants":{"fast":{"temperature":0}}}]}}}
            """);
        Assert.Equal("qwen3:4b", cat.Resolve("ollama/qwen3:4b").ModelId);
        var m = cat.Resolve("ollama/meta/llama@fast");
        Assert.Equal("meta/llama", m.ModelId);
        Assert.Equal("fast", m.Variant);
        Assert.Equal(0, m.VariantSettings["temperature"]!.GetValue<int>());
        Assert.Equal("ollama", m.ProviderId);
    }

    [Fact] public void UnknownRefsAreErrors()
    {
        var cat = ModelCatalog.Parse(PiFile);
        Assert.Throws<ArgumentException>(() => cat.Resolve("nope/x"));
        Assert.Throws<ArgumentException>(() => cat.Resolve("strata/nope"));
        Assert.Throws<ArgumentException>(() => cat.Resolve("strata/inkling-2-mini-sq-15@nope"));
        Assert.Throws<ArgumentException>(() => cat.Resolve("noslash"));
    }

    [Fact] public void UnknownFieldsAreIgnoredAndToolCallsDefaultTrue()
        => Assert.True(ModelCatalog.Parse("""{"providers":{"p":{"api":"manual","future":1,"models":[{"id":"m","whatever":{"a":1}}]}},"other":5}""").Resolve("p/m").ToolCalls);

    [Fact] public void LocateHonoursExplicitPath()
    {
        Assert.Throws<FileNotFoundException>(() => ModelCatalog.Locate("definitely/not/here.json"));
    }
}
