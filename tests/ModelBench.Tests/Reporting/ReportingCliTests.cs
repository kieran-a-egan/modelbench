using ModelBench.Cli;
using ModelBench.Core.Domain;
using ModelBench.Tests.Support;
using static ModelBench.Core.Domain.AttemptOutcome;

namespace ModelBench.Tests.Reporting;

public class ReportingCliTests
{
    private static async Task<(int, string)> Cli(params string[] args)
    {
        using var sw = new StringWriter();
        var code = await CliApp.RunAsync(args, sw, sw);
        return (code, sw.ToString());
    }

    private static (Run a, Run b, Suite suite) Seed(TempDir dir)
    {
        using var db = dir.OpenDb();
        var suite = ReportSeed.AddSuite(db, "bench-suite");
        var a = ReportSeed.AddRun(db, suite, "prov/model-a", ExecutionMode.SingleTurn, ReportSeed.T0, new A(0, 0, Passed, Cost: 1), new A(1, 0, Failed, Cost: 1));
        var b = ReportSeed.AddRun(db, suite, "prov/model-b", ExecutionMode.SingleTurn, ReportSeed.T0, new A(0, 0, Failed, Cost: 2), new A(1, 0, Passed, Cost: 2));
        return (a, b, suite);
    }

    [Fact] public async Task LeaderboardBreakdownHistoryAndCompare()
    {
        using var dir = new TempDir();
        var (a, b, suite) = Seed(dir);
        string[] g = ["--db", dir.File("test.db")];

        var (code, output) = await Cli([.. g, "leaderboard"]);
        Assert.Equal(0, code);
        Assert.Contains("bench-suite", output);
        Assert.Contains("prov/model-a", output);
        Assert.Contains("0.500", output);

        (_, output) = await Cli([.. g, "leaderboard", "--mode", "ToolAssisted"]);
        Assert.DoesNotContain("prov/model", output);

        (code, output) = await Cli([.. g, "breakdown", "--suite", suite.Id.ToString()[..8]]);
        Assert.Equal(0, code);
        Assert.Contains("CATEGORY", output);
        Assert.Contains("COMPLEXITY", output);

        (code, output) = await Cli([.. g, "history", "--model", "prov/model-a", "--suite", suite.Id.ToString()]);
        Assert.Equal(0, code);
        Assert.Contains(a.Id.ToString()[..8], output);

        (code, output) = await Cli([.. g, "compare", a.Id.ToString()[..8], b.Id.ToString()[..8]]);
        Assert.Equal(0, code);
        Assert.Contains("PassToFail 1", output);
        Assert.Contains("FailToPass 1", output);
    }

    [Fact] public async Task CompareOfDifferentSuitesIsAClearError()
    {
        using var dir = new TempDir();
        Guid a, b;
        using (var db = dir.OpenDb())
        {
            a = ReportSeed.AddRun(db, ReportSeed.AddSuite(db, "x", "1111"), "m/a", ExecutionMode.SingleTurn, ReportSeed.T0, new A(0, 0, Passed)).Id;
            b = ReportSeed.AddRun(db, ReportSeed.AddSuite(db, "y", "2222"), "m/a", ExecutionMode.SingleTurn, ReportSeed.T0, new A(0, 0, Passed)).Id;
        }
        var (code, output) = await Cli("--db", dir.File("test.db"), "compare", a.ToString(), b.ToString());
        Assert.Equal(1, code);
        Assert.Contains("not comparable", output);
    }

    [Fact] public async Task ExportWritesJsonAndCsvAndRunShowReportsTotals()
    {
        using var dir = new TempDir();
        var (a, _, _) = Seed(dir);
        string[] g = ["--db", dir.File("test.db")];

        var (code, output) = await Cli([.. g, "export", "--run", a.Id.ToString(), "--format", "json", "--out", dir.File("run.json")]);
        Assert.Equal(0, code);
        Assert.Contains("\"attempts\"", File.ReadAllText(dir.File("run.json")));

        (code, _) = await Cli([.. g, "export", "--run", a.Id.ToString(), "--format", "csv", "--out", dir.File("csv")]);
        Assert.Equal(0, code);
        Assert.True(File.Exists(dir.File("csv/assertions.csv")) && File.Exists(dir.File("csv/runs.csv")) && File.Exists(dir.File("csv/attempts.csv")));

        (code, output) = await Cli([.. g, "export", "--run", a.Id.ToString(), "--format", "xml", "--out", dir.File("x")]);
        Assert.Equal(1, code);

        (code, output) = await Cli([.. g, "run", "show", a.Id.ToString()[..8], "--failures"]);
        Assert.Equal(0, code);
        Assert.Contains("pass rate 1/2", output);
        Assert.Contains("cost per success: 2", output);
        Assert.Contains("FAIL value:", output);
    }
}
