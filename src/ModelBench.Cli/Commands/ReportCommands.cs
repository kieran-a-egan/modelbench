using System.CommandLine;
using System.Globalization;
using ModelBench.Core.Domain;
using ModelBench.Core.Reporting;
using static ModelBench.Cli.Commands.CliContext;

namespace ModelBench.Cli.Commands;

internal static class ReportCommands
{
    private static string F(double? v) => v is { } d ? d.ToString("0.000", CultureInfo.InvariantCulture) : "-";
    /// <summary>Magnitude-aware: large values (ms, tokens) as integers, small ones (USD) keep their significant digits.</summary>
    internal static string Num(double v) => v.ToString(Math.Abs(v) >= 100 ? "0" : Math.Abs(v) >= 1 ? "0.##" : "0.######", CultureInfo.InvariantCulture);
    private static string N(double? v) => v is { } d ? Num(d) : "-";
    private static string D(double? v) => v is { } d ? (d > 0 ? "+" : "") + Num(d) : "-";

    public static IEnumerable<Command> Build(CliContext cli)
    {
        var mode = new Option<ExecutionMode>("--mode") { DefaultValueFactory = _ => ExecutionMode.SingleTurn, Description = "Execution mode (leaderboards are separate per mode)" };

        var suite = new Option<string?>("--suite") { Description = "Suite id or unique prefix (restricts to that suite's content hash)" };
        var all = new Option<bool>("--all-runs") { Description = "Show every completed run, not just the latest per model and suite" };
        var lb = new Command("leaderboard", "Rank models per suite content hash") { mode, suite, all };
        lb.SetAction((pr, ct) => Guard(pr, w =>
        {
            using var db = cli.OpenDb(pr);
            var sid = pr.GetValue(suite) is { } s ? ResolveId(s, db.Suites.Select(x => x.Id).ToList(), "suite") : (Guid?)null;
            foreach (var g in Reports.Leaderboard(db, pr.GetValue(mode), sid, pr.GetValue(all)))
            {
                w.WriteLine($"\nsuite {S(g.SuiteName)}  hash {g.SuiteHash[..12]}  mode {pr.GetValue(mode)}");
                Table(w, ["MODEL", "RUN", "DATE", "PASS", "SCORE", "N", "INFRA/TO", "CONSIST", "TTFT MS", "TPS", "COST", "COST/SUCCESS"],
                    g.Rows.Select(r => new[]
                    {
                        S(r.ModelRef), Short(r.RunId), r.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), F(r.PassRate), F(r.MeanScore), r.Graded.ToString(),
                        $"{r.InfraErrors}/{r.Timeouts}", F(r.Consistency), N(r.Metric(MetricNames.TimeToFirstTokenMs).Median), N(r.Metric(MetricNames.ThroughputTps).Mean),
                        N(r.Metric(MetricNames.CostUsd).Total), N(r.CostPerSuccess),
                    }).ToList());
            }
            return Task.FromResult(0);
        }));

        var bSuite = new Option<string>("--suite") { Required = true, Description = "Suite id or unique prefix" };
        var bd = new Command("breakdown", "Model x category and model x complexity pass rates") { mode, bSuite };
        bd.SetAction((pr, ct) => Guard(pr, w =>
        {
            using var db = cli.OpenDb(pr);
            var id = ResolveId(pr.GetRequiredValue(bSuite), db.Suites.Select(x => x.Id).ToList(), "suite");
            var hash = db.Suites.Where(x => x.Id == id).Select(x => x.ContentHash).Single();
            var b = Reports.Breakdown(db, pr.GetValue(mode), hash);
            foreach (var (title, m) in new[] { ("CATEGORY", b.ByCategory), ("COMPLEXITY", b.ByComplexity) })
                Table(w, [title, .. m.Columns.Select(S)], m.Models.Select((model, i) => (string[])[S(model), .. m.Cells[i].Select(F)]).ToList());
            return Task.FromResult(0);
        }));

        var a = new Argument<string>("runA") { Description = "Run id or unique prefix" };
        var b2 = new Argument<string>("runB") { Description = "Run id or unique prefix" };
        var cmp = new Command("compare", "Per-task diff of two runs on the same suite content") { a, b2 };
        cmp.SetAction((pr, ct) => Guard(pr, w =>
        {
            using var db = cli.OpenDb(pr);
            var ids = db.Runs.Select(r => r.Id).ToList();
            var c = Reports.Compare(db, ResolveId(pr.GetRequiredValue(a), ids, "run"), ResolveId(pr.GetRequiredValue(b2), ids, "run"));
            w.WriteLine($"A {Short(c.A.RunId)} {S(c.A.ModelRef)}   B {Short(c.B.RunId)} {S(c.B.ModelRef)}");
            w.WriteLine($"pass rate {F(c.A.PassRate)} -> {F(c.B.PassRate)} ({D(c.PassRateDelta)})   mean score {F(c.A.MeanScore)} -> {F(c.B.MeanScore)} ({D(c.MeanScoreDelta)})");
            w.WriteLine(string.Join("  ", Enum.GetValues<TaskChange>().Select(t => $"{t} {c.Count(t)}")));
            Table(w, ["METRIC", "A", "B", "DELTA"], c.Metrics.Select(m => new[] { m.Name, N(m.A), N(m.B), D(m.Delta) }).ToList());
            Table(w, ["TASK", "FAMILY", "CX", "CHANGE", "SCORE A", "SCORE B", "DELTA"],
                c.Tasks.Where(t => t.Change != TaskChange.BothPass).Select(t => new[] { t.Index.ToString(), S(t.FamilyId), t.Complexity.ToString(), t.Change.ToString(), F(t.ScoreA), F(t.ScoreB), D(t.ScoreDelta) }).ToList());
            return Task.FromResult(0);
        }));

        var model = new Option<string>("--model") { Required = true, Description = "provider/model[@variant] as recorded on the run" };
        var hSuite = new Option<string>("--suite") { Required = true, Description = "Suite id or unique prefix" };
        var hist = new Command("history", "A model's completed runs over time on one suite content hash") { model, hSuite };
        hist.SetAction((pr, ct) => Guard(pr, w =>
        {
            using var db = cli.OpenDb(pr);
            var id = ResolveId(pr.GetRequiredValue(hSuite), db.Suites.Select(x => x.Id).ToList(), "suite");
            var hash = db.Suites.Where(x => x.Id == id).Select(x => x.ContentHash).Single();
            Table(w, ["RUN", "DATE", "MODE", "PASS", "SCORE", "N", "INFRA/TO", "LABEL"],
                Reports.History(db, pr.GetRequiredValue(model), hash).Select(r => new[]
                {
                    Short(r.RunId), r.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), r.Mode.ToString(), F(r.PassRate), F(r.MeanScore),
                    r.Graded.ToString(), $"{r.InfraErrors}/{r.Timeouts}", S(r.Label),
                }).ToList());
            return Task.FromResult(0);
        }));

        var run = new Option<string>("--run") { Required = true, Description = "Run id or unique prefix" };
        var format = new Option<string>("--format") { Required = true, Description = "csv (a directory of runs/attempts/assertions.csv) or json (a file)" };
        var outp = new Option<string>("--out") { Required = true, Description = "Output directory (csv) or file (json)" };
        var exp = new Command("export", "Export a run with attempts, assertions and metrics") { run, format, outp };
        exp.SetAction((pr, ct) => Guard(pr, w =>
        {
            using var db = cli.OpenDb(pr);
            var id = ResolveId(pr.GetRequiredValue(run), db.Runs.Select(r => r.Id).ToList(), "run");
            var path = Path.GetFullPath(pr.GetRequiredValue(outp));
            switch (pr.GetRequiredValue(format).ToLowerInvariant())
            {
                case "json":
                    File.WriteAllText(path, Export.Json(db, id));
                    w.WriteLine($"wrote {S(path)}");
                    break;
                case "csv":
                    foreach (var p in Export.WriteCsvDirectory(db, id, path)) w.WriteLine($"wrote {S(p)}");
                    break;
                default: throw new ArgumentException("--format must be csv or json");
            }
            return Task.FromResult(0);
        }));

        return [lb, bd, cmp, hist, exp];
    }
}
