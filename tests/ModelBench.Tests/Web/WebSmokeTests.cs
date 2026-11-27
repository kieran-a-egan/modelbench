using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelBench.Core.Domain;
using ModelBench.Core.Persistence;
using ModelBench.Tests.Reporting;
using ModelBench.Tests.Support;
using ModelBench.Web;
using static ModelBench.Core.Domain.AttemptOutcome;

namespace ModelBench.Tests.Web;

public sealed class WebFixture : IDisposable
{
    public TempDir Dir { get; } = new();
    public WebApplicationFactory<ModelsFile> Factory { get; }
    public Run RunA { get; }
    public Run RunB { get; }
    public Run Other { get; }
    public Run Cancelled { get; }
    public Suite Suite { get; }
    public Attempt Attempt { get; }

    public WebFixture()
    {
        File.WriteAllText(Dir.File("models.json"), TestKit.ModelsJson("http://127.0.0.1:9/v1", "sk-test-SECRET"));
        using (var db = Dir.OpenDb())
        {
            Suite = ReportSeed.AddSuite(db, "smoke");
            RunA = ReportSeed.AddRun(db, Suite, "fake/m1", ExecutionMode.SingleTurn, ReportSeed.T0,
                new A(0, 0, Passed, Cost: 1, Ttft: 10), new A(1, 0, Failed, FinalText: "<script>alert(1)</script>"), new A(2, 0, InfraError));
            RunB = ReportSeed.AddRun(db, Suite, "fake/m1", ExecutionMode.SingleTurn, ReportSeed.T0.AddDays(1), new A(0, 0, Failed), new A(1, 0, Passed));
            Other = ReportSeed.AddRun(db, ReportSeed.AddSuite(db, "other", "9999"), "fake/m1", ExecutionMode.SingleTurn, ReportSeed.T0, new A(0, 0, Passed));
            Cancelled = ReportSeed.AddRun(db, Suite, "fake/m1", ExecutionMode.SingleTurn, ReportSeed.T0.AddDays(2), new A(0, 0, Passed));
            Cancelled.Status = RunStatus.Cancelled;
            db.SaveChanges();
            Attempt = db.Attempts.First(a => a.RunId == RunA.Id && a.FinalText!.Contains("<script>"));
        }
        Factory = new WebApplicationFactory<ModelsFile>().WithWebHostBuilder(b => b
            .UseSetting("urls", "http://127.0.0.1:0").UseSetting("MODELBENCH_DB", Dir.File("test.db")).UseSetting("MODELBENCH_MODELS", Dir.File("models.json")));
    }

    public void Dispose()
    {
        Factory.Dispose();
        Dir.Dispose();
    }
}

public class WebSmokeTests(WebFixture fx) : IClassFixture<WebFixture>
{
    private async Task<string> Ok(string url)
    {
        using var client = fx.Factory.CreateClient();
        var res = await client.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)res.StatusCode}: {body[..Math.Min(body.Length, 400)]}");
        return body;
    }

    [Fact] public async Task EveryPageReturns200()
    {
        string[] urls =
        [
            "/", "/models", "/suites", "/runs", "/leaderboard", "/leaderboard?mode=ToolAssisted", "/leaderboard?mode=SingleTurn&view=breakdown", "/leaderboard?all=true",
            "/compare", $"/suites/{fx.Suite.Id}", $"/tasks/{fx.Suite.Tasks[0].Id}", $"/runs/{fx.RunA.Id}", $"/attempts/{fx.Attempt.Id}",
            $"/history?model=fake%2Fm1&suite={fx.Suite.ContentHash}",
            $"/export/runs/{fx.RunA.Id}.json", $"/export/runs/{fx.RunA.Id}/runs.csv", $"/export/runs/{fx.RunA.Id}/attempts.csv", $"/export/runs/{fx.RunA.Id}/assertions.csv",
        ];
        foreach (var u in urls) await Ok(u);
    }

    [Fact] public async Task CandidateOutputIsHtmlEncoded()
    {
        var html = await Ok($"/attempts/{fx.Attempt.Id}");
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
    }

    [Fact] public async Task RunDetailShowsSummaryAndDownloadLinks()
    {
        var html = await Ok($"/runs/{fx.RunA.Id}");
        Assert.Contains("Pass rate", html);
        Assert.Contains($"/export/runs/{fx.RunA.Id}.json", html);
        Assert.Contains("Failures", html);
    }

    [Fact] public async Task ResumeButtonOnlyForUnfinishedRuns()
    {
        var html = await Ok($"/runs/{fx.Cancelled.Id}");
        Assert.Contains("Resume run", html);
        Assert.Contains("Retry infrastructure errors", html);
        Assert.DoesNotContain("Resume run", await Ok($"/runs/{fx.RunA.Id}"));
    }

    [Fact] public async Task PendingRunNotQueuedInThisProcessOffersResume()
    {
        Run pending;
        using (var db = fx.Dir.OpenDb())
        {
            pending = ReportSeed.AddRun(db, fx.Suite, "fake/m1", ExecutionMode.SingleTurn, ReportSeed.T0.AddDays(3));
            pending.Status = RunStatus.Pending; // queued by a dashboard process that has since stopped
            db.SaveChanges();
        }
        Assert.Contains("Resume run", await Ok($"/runs/{pending.Id}"));
    }

    [Fact] public async Task CompareShowsDiffAndRefusesDifferentHashes()
    {
        var html = await Ok($"/compare?a={fx.RunA.Id}&b={fx.RunB.Id}");
        Assert.Contains("PassToFail", html);
        html = await Ok($"/compare?a={fx.RunA.Id}&b={fx.Other.Id}");
        Assert.Contains("not comparable", html);
    }

    [Fact] public async Task ModelsPageNeverShowsApiKeysAndMasksTheEditor()
    {
        var html = await Ok("/models");
        Assert.DoesNotContain("sk-test-SECRET", html);
        Assert.Contains("fake/m1", html);
        Assert.Contains("***", html);
    }

    [Fact] public async Task UnknownExportsAre404()
    {
        using var client = fx.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/export/runs/{Guid.NewGuid()}.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/export/runs/{fx.RunA.Id}/secrets.csv")).StatusCode);
    }

    [Fact] public void ModelsFileRefusesAnEnergyBlockMissingWatts()
    {
        using var dir = new TempDir();
        var good = TestKit.ModelsJson("http://127.0.0.1:9/v1");
        File.WriteAllText(dir.File("models.json"), good);
        var f = new ModelsFile(dir.File("models.json"));
        var err = f.Save(good.Replace("\"watts\":300,", ""));
        Assert.Contains("energy.watts", err);
        Assert.Equal(good, File.ReadAllText(dir.File("models.json")));
    }

    [Fact] public void ModelsFileSavesWhenAnEnvReferencedVariableIsUnset()
    {
        using var dir = new TempDir();
        var json = TestKit.ModelsJson("http://127.0.0.1:9/v1", "{env:MODELBENCH_TEST_UNSET_VAR}");
        File.WriteAllText(dir.File("models.json"), json);
        Assert.Null(new ModelsFile(dir.File("models.json")).Save(json.Replace("\"watts\":300,", "\"watts\":250,")));
    }

    [Fact] public void ModelsFileRestoresMaskedSecretsOnSave()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("models.json"), TestKit.ModelsJson("http://127.0.0.1:9/v1", "sk-test-SECRET"));
        var f = new ModelsFile(dir.File("models.json"));
        var masked = f.MaskedText();
        Assert.DoesNotContain("sk-test-SECRET", masked);
        Assert.Null(f.Save(masked.Replace("\"maxTokens\": 512", "\"maxTokens\": 256")));
        var saved = File.ReadAllText(dir.File("models.json"));
        Assert.Contains("sk-test-SECRET", saved);
        Assert.Contains("256", saved);
        Assert.NotNull(f.Save("{\"providers\":{\"x\":{\"api\":\"openai-completions\"}}}")); // invalid: no models array
        Assert.Contains("sk-test-SECRET", File.ReadAllText(dir.File("models.json")));
        Assert.NotNull(f.Save(masked.Replace("\"fake\"", "\"renamed\""))); // no original value to restore for a renamed provider
    }
}
