using System.Text.Json;
using ModelBench.Core.Config;
using ModelBench.Core.Domain;
using ModelBench.Core.Reporting;
using ModelBench.Tests.Support;
using static ModelBench.Core.Domain.AttemptOutcome;

namespace ModelBench.Tests.Reporting;

public class ExportTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@x", "'@x")]
    [InlineData("\tx", "'\tx")]
    [InlineData("=a,b", "\"'=a,b\"")]
    public void CellEscapesAndNeutralisesFormulas(string input, string expected) => Assert.Equal(expected, Export.Cell(input));

    [Fact] public void NonTextCellsAreNotNeutralised()
    {
        Assert.Equal("", Export.Cell(null));
        Assert.Equal("-1.5", Export.Cell(-1.5)); // numbers are not text
        Assert.Equal("true", Export.Cell(true));
    }

    private static (Run, string) Seed(TempDir dir)
    {
        using var db = dir.OpenDb();
        var model = TestKit.Model("http://127.0.0.1:9/v1", apiKey: "sk-test-SECRET");
        var run = ReportSeed.AddRun(db, ReportSeed.AddSuite(db), model.Ref, ExecutionMode.SingleTurn, ReportSeed.T0,
            new A(0, 0, Passed, Cost: 1), new A(1, 0, Failed, FinalText: "=cmd|'/C calc'!A0"), new A(2, 0, Failed, FinalText: "has,comma \"and quote\""));
        run.ModelSnapshotJson = ModelCatalog.Snapshot(model);
        db.SaveChanges();
        return (run, dir.File("test.db"));
    }

    [Fact] public void NoExportContainsTheApiKey()
    {
        using var dir = new TempDir();
        var (run, _) = Seed(dir);
        using var db = dir.OpenDb();
        Assert.DoesNotContain("sk-test-SECRET", run.ModelSnapshotJson);
        var json = Export.Json(db, run.Id);
        Assert.DoesNotContain("sk-test-SECRET", json);
        Assert.Contains("\"modelSnapshot\"", json);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(3, parsed.RootElement.GetProperty("attempts").GetArrayLength());
        foreach (var t in Export.Tables)
        {
            using var w = new StringWriter();
            Export.WriteCsv(db, run.Id, t, w);
            Assert.DoesNotContain("sk-test-SECRET", w.ToString());
        }
    }

    [Fact] public void CsvDirectoryHasThreeFilesWithNeutralisedAndQuotedCells()
    {
        using var dir = new TempDir();
        var (run, _) = Seed(dir);
        using var db = dir.OpenDb();
        var paths = Export.WriteCsvDirectory(db, run.Id, dir.File("out"));
        Assert.Equal(["runs.csv", "attempts.csv", "assertions.csv"], paths.Select(Path.GetFileName).ToArray());
        var attempts = File.ReadAllText(paths[1]);
        Assert.Contains("'=cmd|'/C calc'!A0", attempts);
        Assert.DoesNotContain(",=cmd", attempts);
        Assert.Contains("\"has,comma \"\"and quote\"\"\"", attempts);
        Assert.Contains("\r\n", attempts);
        Assert.Equal(4, File.ReadAllLines(paths[2]).Length); // header + one assertion per graded attempt
    }
}
