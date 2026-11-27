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
        var cat = ModelCatalog.Parse(File.ReadAllText(Path.Combine(dir.FullName, "models.example.json")));
        Assert.Equal(3, cat.List().Count);
        foreach (var e in cat.List()) cat.Resolve(e.Ref);
        var strata = cat.Resolve("strata/inkling-2-mini-sq-15@high");
        Assert.Equal("high", strata.VariantSettings["reasoningEffort"]!.GetValue<string>());
        Assert.Equal(new EnergyProfile(350, 0.30m), strata.Energy);
        Assert.Equal("http://127.0.0.1:11434/v1", cat.Resolve("ollama/qwen3:8b").BaseUrl);
        Assert.Equal(ApiKind.Manual, cat.Resolve("manual/chat-ui").Api);
    }

    [Fact] public void NonLocalProvidersInAPiFileAreSkippedAndRefused()
    {
        var cat = ModelCatalog.Parse("""{"providers":{"local":{"api":"openai-completions","baseUrl":"http://127.0.0.1:1/v1","models":[{"id":"m"}]},"anthropic":{"api":"anthropic-messages","baseUrl":"https://api.anthropic.com","apiKey":"{env:NOT_SET_ANYWHERE}","models":[{"id":"claude"}]}}}""");
        Assert.Equal(["local/m"], cat.List().Select(e => e.Ref));
        Assert.Equal(["anthropic/claude (api 'anthropic-messages' is not supported)"], cat.Skipped());
        var ex = Assert.Throws<InvalidOperationException>(() => cat.Resolve("anthropic/claude"));
        Assert.Contains("not supported", ex.Message);
    }

    [Fact] public void OpenAiCompatibleEndpointsMustBeLocalIncludingModelOverrides()
    {
        var cat = ModelCatalog.Parse("""
            {"providers":{
              "lan":{"api":"openai-completions","baseUrl":"http://192.168.1.20:8080/v1","models":[{"id":"a"},{"id":"moved","baseUrl":"https://api.openai.com/v1"}]},
              "loop":{"api":"openai-completions","baseUrl":"http://localhost:11434/v1","models":[{"id":"b"},{"id":"v6","baseUrl":"http://[::1]:1/v1"}]},
              "hosted":{"api":"openai-completions","baseUrl":"https://203.0.113.7/v1","models":[{"id":"c"},{"id":"back","baseUrl":"http://10.0.0.5/v1"}]}}}
            """);
        Assert.Equal(["lan/a", "loop/b", "loop/v6", "hosted/back"], cat.List().Select(e => e.Ref));
        Assert.Equal(2, cat.Skipped().Count);
        Assert.Contains(cat.Skipped(), s => s.StartsWith("lan/moved (baseUrl host 'api.openai.com'"));
        Assert.Contains("not a loopback or private-network address", Assert.Throws<InvalidOperationException>(() => cat.Resolve("hosted/c")).Message);
        Assert.Throws<InvalidOperationException>(() => cat.Resolve("lan/moved"));
        Assert.Equal("http://10.0.0.5/v1", cat.Resolve("hosted/back").BaseUrl);
    }

    [Theory]
    [InlineData("127.0.0.1", true)] [InlineData("10.1.2.3", true)] [InlineData("172.16.0.1", true)] [InlineData("172.31.255.255", true)]
    [InlineData("192.168.0.1", true)] [InlineData("169.254.1.1", true)] [InlineData("::1", true)] [InlineData("fd00::1", true)] [InlineData("fe80::1", true)]
    [InlineData("::ffff:192.168.0.1", true)] [InlineData("172.32.0.1", false)] [InlineData("8.8.8.8", false)] [InlineData("100.64.0.1", false)]
    [InlineData("2606:4700::1", false)] [InlineData("::ffff:8.8.8.8", false)]
    public void LocalAddressRanges(string ip, bool local) => Assert.Equal(local, LocalEndpoint.IsLocal(System.Net.IPAddress.Parse(ip)));

    [Fact] public async Task TransportRefusesNonLocalHostsWithoutConnecting()
    {
        using var http = new HttpClient(new SocketsHttpHandler { ConnectCallback = LocalEndpoint.ConnectAsync, UseProxy = false });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("http://203.0.113.7/v1/models", TestContext.Current.CancellationToken));
        Assert.Contains("refusing to connect", ex.Message);
    }

    [Fact] public async Task ConnectFallsBackAcrossLocalAddressesAndNeverTriesOthers()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); // IPv4 only, so [::1] on this port is refused
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            System.Net.IPAddress[] addrs = [System.Net.IPAddress.Parse("203.0.113.7"), System.Net.IPAddress.IPv6Loopback, System.Net.IPAddress.Loopback];
            // 203.0.113.7 is unroutable (TEST-NET-3): attempting it would hang past this 5 s budget and cancel the test.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(5));
            await using var stream = await LocalEndpoint.ConnectAsync("localhost", addrs, port, budget.Token);
            using var accepted = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)accepted.Client.RemoteEndPoint!).Address);

            listener.Stop(); // nothing accepts now: every local address fails, and the error lists only the local ones tried
            var ex = await Assert.ThrowsAsync<HttpRequestException>(async () => await LocalEndpoint.ConnectAsync("localhost", addrs, port, TestContext.Current.CancellationToken));
            Assert.Contains("127.0.0.1", ex.Message);
            Assert.DoesNotContain("203.0.113.7", ex.Message);
        }
        finally { listener.Stop(); }
    }

    [Fact] public async Task ConnectWithOnlyNonLocalAddressesIsRefusedWithoutAttempt()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await LocalEndpoint.ConnectAsync("example", [System.Net.IPAddress.Parse("203.0.113.7")], 80, TestContext.Current.CancellationToken));
        Assert.Contains("refusing to connect", ex.Message);
    }

    [Fact] public void EnvSubstitutionInKeysAndHeaders()
    {
        Environment.SetEnvironmentVariable("MB_TEST_SECRET_A", "sk-secret-value");
        var cat = ModelCatalog.Parse("""{"providers":{"p":{"api":"openai-completions","baseUrl":"http://127.0.0.1:1","apiKey":"{env:MB_TEST_SECRET_A}","headers":{"X-Org":"org-{env:MB_TEST_SECRET_A}"},"models":[{"id":"m"}]}}}""");
        var m = cat.Resolve("p/m");
        Assert.Equal("sk-secret-value", m.ApiKey);
        Assert.Equal("org-sk-secret-value", m.Headers["X-Org"]);
        Assert.Null(m.Pricing);
    }

    [Fact] public void MissingEnvVarIsAnError()
    {
        var cat = ModelCatalog.Parse("""{"providers":{"p":{"api":"manual","apiKey":"{env:MB_TEST_DEFINITELY_UNSET}","models":[{"id":"m"}]}}}""");
        Assert.Throws<MissingEnvironmentVariableException>(() => cat.Resolve("p/m"));
    }

    [Fact] public void SnapshotHasNoSecrets()
    {
        Environment.SetEnvironmentVariable("MB_TEST_SECRET_B", "sk-very-secret");
        var m = ModelCatalog.Parse("""{"providers":{"p":{"api":"openai-completions","baseUrl":"http://127.0.0.1:1","apiKey":"{env:MB_TEST_SECRET_B}","headers":{"Authorization":"Bearer {env:MB_TEST_SECRET_B}"},"models":[{"id":"m","maxTokens":10}]}}}""").Resolve("p/m");
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

    [Fact] public void LocateFallsBackToModelbenchModelsEnvButExplicitWins()
    {
        var file = Path.Combine(Path.GetTempPath(), $"mb-env-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, PiFile);
        var before = Environment.GetEnvironmentVariable("MODELBENCH_MODELS");
        try
        {
            Environment.SetEnvironmentVariable("MODELBENCH_MODELS", file);
            Assert.Equal(Path.GetFullPath(file), ModelCatalog.Locate(null));
            Assert.Throws<FileNotFoundException>(() => ModelCatalog.Locate("definitely/not/here.json"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MODELBENCH_MODELS", before);
            File.Delete(file);
        }
    }
}
