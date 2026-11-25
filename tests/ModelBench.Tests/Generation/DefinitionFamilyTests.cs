using ModelBench.Core.Domain;
using ModelBench.Core.Execution;
using ModelBench.Core.Families;
using ModelBench.Core.Generation;
using ModelBench.Core.Sandbox;
using ModelBench.Tests.Support;

namespace ModelBench.Tests.Generation;

public class DefinitionFamilyTests
{
    private static string BenchmarksDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "benchmarks", "static"))) return Path.Combine(d.FullName, "benchmarks");
        throw new DirectoryNotFoundException("benchmarks");
    }

    [Fact] public void ShippedDefinitionsAreInTheRegistry()
    {
        foreach (var id in new[] { "static:unit-conversion", "static:instruction-following", "repo:inventory-service" })
            Assert.NotNull(FamilyRegistry.Find(id));
    }

    [Fact] public void TasksComeOutInDefinitionOrderAndWrap()
    {
        var fam = FamilyRegistry.Get("static:unit-conversion");
        var prompts = Enumerable.Range(0, 6).Select(i => fam.Generate(new Rng(1L), 1, i).Prompt.User).ToArray();
        Assert.Contains("3.5 inches", prompts[0]);
        Assert.Contains("90 km/h", prompts[1]);
        Assert.Contains("68 degrees Fahrenheit", prompts[2]);
        Assert.Contains("5 miles", prompts[3]);
        Assert.Equal(prompts[0], prompts[4]);
        Assert.Equal(prompts[1], prompts[5]);
    }

    [Theory]
    [InlineData("static:unit-conversion")]
    [InlineData("static:instruction-following")]
    [InlineData("repo:inventory-service")]
    public async Task ShippedDefinitionsValidateAndPassSelfTest(string id)
    {
        var fam = FamilyRegistry.Get(id);
        for (var i = 0; i < 4; i++) Assert.True(fam.Validate(fam.Generate(new Rng(1L), 1, i)).Ok);
        var report = await SelfTest.RunAsync(id, 4, new UnavailableSandbox(), default);
        Assert.True(report.Ok, string.Join("\n", report.Failures.Select(f => $"{f.Case}: {f.Problem}")));
        Assert.Equal(0, report.Skipped);
        Assert.True(report.Checks > 20);
    }

    [Fact] public void RepositoryPromptShowsTheFilesWithHeadersAndRespectsTheIncludeGlob()
    {
        var prompt = FamilyRegistry.Get("repo:inventory-service").Generate(new Rng(1L), 1, 0).Prompt.User;
        Assert.Contains("### `inventory/orders.py`", prompt);
        Assert.Contains("```py", prompt);
        Assert.Contains("MAX_BATCH = 100", prompt);
        Assert.EndsWith("tags, as a plain decimal without units.", prompt);
    }

    [Fact] public void SuiteFromAStaticFamilyKeepsOrderAndVerifies()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var suite = SuiteGenerator.Generate(new SuiteSpec("s", 5, [new FamilySpec("static:unit-conversion", 1, 4), new FamilySpec("repo:inventory-service", 2, 4)]));
        Assert.Contains("3.5 inches", suite.Tasks.ElementAt(0).PromptJson);
        Assert.Contains("5 miles", suite.Tasks.ElementAt(3).PromptJson);
        Assert.Contains("inventory/orders.py", suite.Tasks.ElementAt(4).PromptJson);
        Assert.Contains("is_low", suite.Tasks.ElementAt(5).PromptJson);
        SuiteGenerator.Persist(db, suite);
        Assert.Empty(SuiteGenerator.Verify(db, suite.Id));
    }

    private static string CopyOfStaticDir(TempDir dir)
    {
        var target = Path.Combine(dir.Path, "benchmarks");
        Directory.CreateDirectory(Path.Combine(target, "static"));
        foreach (var f in Directory.GetFiles(Path.Combine(BenchmarksDir(), "static"), "unit-conversion*"))
            File.Copy(f, Path.Combine(target, "static", Path.GetFileName(f)));
        return target;
    }

    [Fact] public void EditingAPromptFileOrTheDefinitionChangesHashAndTaskHash()
    {
        using var dir = new TempDir();
        var root = CopyOfStaticDir(dir);
        var before = (DefinitionFamily)DefinitionLoader.Load(root).Single();
        var again = (DefinitionFamily)DefinitionLoader.Load(root).Single();
        Assert.Equal(before.ContentHash, again.ContentHash);

        var md = Path.Combine(root, "static", "unit-conversion.q1.md");
        File.WriteAllText(md, File.ReadAllText(md).Replace("3.5", "4.5"));
        var edited = (DefinitionFamily)DefinitionLoader.Load(root).Single();
        Assert.NotEqual(before.ContentHash, edited.ContentHash);
        var (a, b) = (before.Generate(new Rng(1L), 1, 0), edited.Generate(new Rng(1L), 1, 0));
        Assert.NotEqual(SuiteGenerator.TaskHash(JsonUtil.Serialize(a.Prompt), JsonUtil.Serialize(a.Hidden)), SuiteGenerator.TaskHash(JsonUtil.Serialize(b.Prompt), JsonUtil.Serialize(b.Hidden)));
        // a task generated from the old definition is rejected by the edited family
        Assert.False(edited.Validate(a).Ok);

        var json = Path.Combine(root, "static", "unit-conversion.json");
        File.WriteAllText(json, File.ReadAllText(json).Replace("\"expected\": \"20\"", "\"expected\": \"21\""));
        Assert.NotEqual(edited.ContentHash, ((DefinitionFamily)DefinitionLoader.Load(root).Single()).ContentHash);
    }

    [Fact] public async Task TasksWithoutAReferenceAreSkippedAndReportedByTheSelfTest()
    {
        using var dir = new TempDir();
        var root = Path.Combine(dir.Path, "benchmarks", "static");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "plain.json"), """
            {"id":"plain","version":3,"category":"misc","mode":"SingleTurn","complexity":2,
             "tasks":[{"prompt":"Say hi in <answer></answer> tags.","answerFormat":"Tagged",
                       "graders":[{"type":"exact","name":"hi","config":{"expected":"hi"}}]}]}
            """);
        var fam = DefinitionLoader.Load(Path.Combine(dir.Path, "benchmarks")).Single();
        Assert.Equal("static:plain", fam.Id);
        Assert.Equal(3, fam.Version);
        var task = fam.Generate(new Rng(1L), 2, 0);
        Assert.Empty(task.ReferenceTurns);
        Assert.True(fam.Validate(task).Ok);
        var report = await SelfTest.RunAsync([fam], 2, new UnavailableSandbox(), default);
        Assert.True(report.Ok);
        Assert.Equal(10, report.Skipped);
    }

    [Fact] public void RepositoryOverTheSizeCapIsRejected()
    {
        using var dir = new TempDir();
        var repo = Path.Combine(dir.Path, "benchmarks", "repos", "big");
        Directory.CreateDirectory(Path.Combine(repo, "repo"));
        File.WriteAllText(Path.Combine(repo, "repo", "a.py"), new string('x', 500));
        File.WriteAllText(Path.Combine(repo, "benchmark.json"), """
            {"version":1,"category":"repository-comprehension","complexity":1,"include":["*.py"],"maxBytes":100,
             "questions":[{"question":"q","answerFormat":"Tagged","graders":[{"type":"exact","name":"a","config":{"expected":"x"}}]}]}
            """);
        Assert.Throws<InvalidDataException>(() => DefinitionLoader.Load(Path.Combine(dir.Path, "benchmarks")));
    }
}
