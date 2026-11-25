using System.CommandLine;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ModelBench.Core.Config;
using ModelBench.Core.Domain;
using ModelBench.Core.Execution;
using ModelBench.Core.Persistence;
using ModelBench.Core.Providers;
using ModelBench.Core.Sandbox;
using static ModelBench.Cli.Commands.CliContext;

namespace ModelBench.Cli.Commands;

internal static class RunCommands
{
    public static Command Build(CliContext cli)
    {
        var run = new Command("run", "Execute and inspect runs");

        var suiteOpt = new Option<string>("--suite") { Required = true, Description = "Suite id or unique prefix" };
        var modelOpt = new Option<string>("--model") { Required = true, Description = "provider/model[@variant]" };
        var modeOpt = new Option<ExecutionMode>("--mode") { DefaultValueFactory = _ => ExecutionMode.SingleTurn, Description = "Execution mode" };
        var repeatsOpt = new Option<int>("--repeats") { DefaultValueFactory = _ => 1, Description = "Attempts per task" };
        var timeoutOpt = new Option<int>("--timeout") { DefaultValueFactory = _ => 300, Description = "Per-attempt timeout in seconds" };
        var labelOpt = new Option<string?>("--label") { Description = "Free-text run label" };
        var start = new Command("start", "Create a run and execute it") { suiteOpt, modelOpt, modeOpt, repeatsOpt, timeoutOpt, labelOpt };
        start.SetAction((pr, ct) => Guard(pr, async w =>
        {
            using var db = cli.OpenDb(pr);
            var suiteId = ResolveId(pr.GetRequiredValue(suiteOpt), db.Suites.Select(s => s.Id).ToList(), "suite");
            var model = cli.Catalog(pr).Resolve(pr.GetRequiredValue(modelOpt));
            var created = RunService.CreateRun(db, suiteId, model, pr.GetRequiredValue(modeOpt), pr.GetRequiredValue(repeatsOpt),
                pr.GetRequiredValue(timeoutOpt), new RetryPolicy(), pr.GetValue(labelOpt));
            w.WriteLine($"run {created.Id} on {S(model.Ref)}");
            var executor = new RunExecutor(ProviderFactory.Create(model), model, new DockerSandbox());
            executor.AttemptCompleted += p => w.WriteLine(
                $"[{p.Done}/{p.Total}] {p.FamilyId} c{p.Complexity} repeat {p.Repeat}: {p.Outcome}{(p.Score is { } s ? $" score {s.ToString("0.00", CultureInfo.InvariantCulture)}" : "")}");
            await executor.ExecuteAsync(db, created.Id, ct);
            Report(db, created.Id, false, w);
            return db.Runs.AsNoTracking().Single(r => r.Id == created.Id).Status == RunStatus.Completed ? 0 : 1;
        }));

        var list = new Command("list", "List runs");
        list.SetAction((pr, ct) => Guard(pr, w =>
        {
            using var db = cli.OpenDb(pr);
            var rows = db.Runs.AsNoTracking().Select(r => new
            {
                r.Id, r.Label, r.ModelRef, r.Mode, r.Status, r.CreatedAt,
                Total = r.Attempts.Count, Passed = r.Attempts.Count(a => a.Outcome == AttemptOutcome.Passed),
            }).ToList().OrderBy(r => r.CreatedAt)
                .Select(r => new[] { Short(r.Id), S(r.Label) is { Length: > 0 } l ? l : "-", S(r.ModelRef), r.Mode.ToString(), r.Status.ToString(), $"{r.Passed}/{r.Total}" })
                .ToList();
            Table(w, ["ID", "LABEL", "MODEL", "MODE", "STATUS", "PASSED"], rows);
            return Task.FromResult(0);
        }));

        var showId = new Argument<string>("id") { Description = "Run id or unique prefix" };
        var failures = new Option<bool>("--failures") { Description = "List every failed assertion with expected/actual/diagnostic" };
        var show = new Command("show", "Show run scores") { showId, failures };
        show.SetAction((pr, ct) => Guard(pr, w =>
        {
            using var db = cli.OpenDb(pr);
            var id = ResolveId(pr.GetRequiredValue(showId), db.Runs.Select(r => r.Id).ToList(), "run");
            Report(db, id, pr.GetValue(failures), w);
            return Task.FromResult(0);
        }));

        run.Subcommands.Add(start);
        run.Subcommands.Add(list);
        run.Subcommands.Add(show);
        return run;
    }

    private static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    internal static void Report(BenchDb db, Guid runId, bool showFailures, TextWriter w)
    {
        var run = db.Runs.AsNoTracking().Single(r => r.Id == runId);
        var attempts = db.Attempts.AsNoTracking().Include(a => a.Task).Include(a => a.Assertions).Include(a => a.Metrics)
            .Where(a => a.RunId == runId).ToList()
            .OrderBy(a => a.Task!.Index).ThenBy(a => a.RepeatIndex).ToList();

        w.WriteLine($"run {run.Id}  model {S(run.ModelRef)}  mode {run.Mode}  status {run.Status}  suite {Short(run.SuiteId)}");
        w.WriteLine("attempts: " + string.Join("  ", Enum.GetValues<AttemptOutcome>().Select(o => (o, n: attempts.Count(a => a.Outcome == o))).Where(t => t.n > 0).Select(t => $"{t.o} {t.n}")));

        var graded = attempts.Where(a => a.Outcome is AttemptOutcome.Passed or AttemptOutcome.Failed).ToList();
        if (graded.Count > 0)
            w.WriteLine($"pass rate {graded.Count(a => a.Outcome == AttemptOutcome.Passed)}/{graded.Count} = {F(graded.Count(a => a.Outcome == AttemptOutcome.Passed) / (double)graded.Count)}   mean score {F(graded.Average(a => a.Score ?? 0))}  (infra errors, timeouts and cancellations excluded)");
        var excluded = attempts.Count(a => a.Outcome is AttemptOutcome.InfraError or AttemptOutcome.Timeout or AttemptOutcome.Cancelled);
        if (excluded > 0) w.WriteLine($"excluded from scores: {excluded}");

        var rows = graded.GroupBy(a => (a.Task!.FamilyId, a.Task.Complexity)).OrderBy(g => g.Key.FamilyId, StringComparer.Ordinal).ThenBy(g => g.Key.Complexity)
            .Select(g => new[] { g.Key.FamilyId, g.Key.Complexity.ToString(), g.Count().ToString(), g.Count(a => a.Outcome == AttemptOutcome.Passed).ToString(), F(g.Average(a => a.Score ?? 0)) })
            .ToList();
        if (rows.Count > 0) Table(w, ["FAMILY", "CX", "N", "PASSED", "MEAN SCORE"], rows);

        foreach (var name in new[] { MetricNames.CostUsd, MetricNames.InputTokens, MetricNames.OutputTokens })
        {
            var ms = attempts.Select(a => a.Metrics.FirstOrDefault(m => m.Name == name)).ToList();
            var measured = ms.Where(m => m?.Value is not null).ToList();
            if (attempts.Count > 0) w.WriteLine($"{name}: {(measured.Count == 0 ? "unavailable" : measured.Sum(m => m!.Value!.Value).ToString("0.######", CultureInfo.InvariantCulture))}  ({measured.Count}/{attempts.Count} attempts)");
        }

        if (!showFailures) return;
        foreach (var a in attempts.Where(a => a.Outcome != AttemptOutcome.Passed))
        {
            w.WriteLine($"\ntask {a.Task!.Index} {a.Task.FamilyId} c{a.Task.Complexity} repeat {a.RepeatIndex}: {a.Outcome}{(a.Score is { } s ? $" score {F(s)}" : "")}");
            if (a.ErrorMessage is not null) w.WriteLine($"  error: {Truncate(a.ErrorMessage, 300)}");
            foreach (var r in a.Assertions.Where(r => !r.Passed))
                w.WriteLine($"  FAIL {Truncate(r.Name, 80)}: expected [{Truncate(r.Expected, 200)}] actual [{Truncate(r.Actual, 200)}] {Truncate(r.Diagnostic, 300)}");
        }
    }
}
