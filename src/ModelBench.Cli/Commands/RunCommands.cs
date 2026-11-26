using System.CommandLine;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ModelBench.Core.Config;
using ModelBench.Core.Domain;
using ModelBench.Core.Execution;
using ModelBench.Core.Persistence;
using ModelBench.Core.Providers;
using ModelBench.Core.Reporting;
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
        var concurrencyOpt = new Option<int>("--concurrency") { DefaultValueFactory = _ => 1, Description = "Parallel attempts" };
        var maxRetriesOpt = new Option<int>("--max-retries") { DefaultValueFactory = _ => new RetryPolicy().MaxRetries, Description = "Retries per attempt on retryable infrastructure errors" };
        var start = new Command("start", "Create a run and execute it") { suiteOpt, modelOpt, modeOpt, repeatsOpt, timeoutOpt, labelOpt, concurrencyOpt, maxRetriesOpt };
        start.SetAction((pr, ct) => Guard(pr, async w =>
        {
            using var db = cli.OpenDb(pr);
            var suiteId = ResolveId(pr.GetRequiredValue(suiteOpt), db.Suites.Select(s => s.Id).ToList(), "suite");
            var model = cli.Catalog(pr).Resolve(pr.GetRequiredValue(modelOpt));
            var created = RunService.CreateRun(db, suiteId, model, pr.GetRequiredValue(modeOpt), pr.GetRequiredValue(repeatsOpt),
                pr.GetRequiredValue(timeoutOpt), new RetryPolicy(MaxRetries: pr.GetRequiredValue(maxRetriesOpt)), pr.GetValue(labelOpt), pr.GetRequiredValue(concurrencyOpt));
            w.WriteLine($"run {created.Id} on {S(model.Ref)}");
            return await Execute(db, created.Id, model, false, w, ct);
        }));

        var resumeId = new Argument<string>("id") { Description = "Run id or unique prefix" };
        var retryInfra = new Option<bool>("--retry-infra") { Description = "Also redo InfraError and Timeout attempts (their retry counts are added to the new attempt)" };
        var resume = new Command("resume", "Run the missing, pending and cancelled attempts of a run") { resumeId, retryInfra };
        resume.SetAction((pr, ct) => Guard(pr, async w =>
        {
            using var db = cli.OpenDb(pr);
            var id = ResolveId(pr.GetRequiredValue(resumeId), db.Runs.Select(r => r.Id).ToList(), "run");
            var model = RunService.PrepareResume(db, id, cli.Catalog(pr));
            w.WriteLine($"resuming run {id} on {S(model.Ref)}");
            return await Execute(db, id, model, pr.GetValue(retryInfra), w, ct);
        }));

        var cancelId = new Argument<string>("id") { Description = "Run id or unique prefix" };
        var cancel = new Command("cancel", "Ask the process executing a run to stop (resume it later)") { cancelId };
        cancel.SetAction((pr, ct) => Guard(pr, w =>
        {
            using var db = cli.OpenDb(pr);
            var id = ResolveId(pr.GetRequiredValue(cancelId), db.Runs.Select(r => r.Id).ToList(), "run");
            RunService.RequestCancel(db, id);
            w.WriteLine($"cancel requested for run {id}");
            return Task.FromResult(0);
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

        var regradeId = new Argument<string>("id") { Description = "Run id or unique prefix" };
        var check = new Option<bool>("--check") { Description = "Exit with a non-zero code when any regraded assertion or score differs from the stored one" };
        var regrade = new Command("regrade", "Re-run grading on stored attempts and report differences (read-only; never overwrites results)") { regradeId, check };
        regrade.SetAction((pr, ct) => Guard(pr, async w =>
        {
            using var db = cli.OpenDb(pr);
            var id = ResolveId(pr.GetRequiredValue(regradeId), db.Runs.Select(r => r.Id).ToList(), "run");
            var report = await Regrader.RunAsync(db, id, new DockerSandbox(), ct);
            foreach (var d in report.Differences)
                w.WriteLine($"DIFF task {d.TaskIndex} {S(d.FamilyId)} repeat {d.Repeat} attempt {Short(d.AttemptId)}: {Truncate(d.Problem, 600)}");
            w.WriteLine($"regraded {report.Attempts} attempts: {report.Differences.Count} with differences");
            return pr.GetValue(check) && report.Differences.Count > 0 ? 1 : 0;
        }));

        run.Subcommands.Add(start);
        run.Subcommands.Add(resume);
        run.Subcommands.Add(cancel);
        run.Subcommands.Add(list);
        run.Subcommands.Add(show);
        run.Subcommands.Add(regrade);
        return run;
    }

    private static async Task<int> Execute(BenchDb db, Guid runId, ResolvedModel model, bool retryInfra, TextWriter w, CancellationToken ct)
    {
        var executor = new RunExecutor(ProviderFactory.Create(model), model, new DockerSandbox());
        executor.AttemptCompleted += p => w.WriteLine(
            $"[{p.Done}/{p.Total}] {p.FamilyId} c{p.Complexity} repeat {p.Repeat}: {p.Outcome}{(p.Score is { } s ? $" score {s.ToString("0.00", CultureInfo.InvariantCulture)}" : "")}");
        await executor.ExecuteAsync(db, runId, ct, retryInfra);
        Report(db, runId, false, w);
        return db.Runs.AsNoTracking().Single(r => r.Id == runId).Status == RunStatus.Completed ? 0 : 1;
    }

    private static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    internal static void Report(BenchDb db, Guid runId, bool showFailures, TextWriter w)
    {
        var s = Reports.Summarize(db, runId);
        w.WriteLine($"run {s.RunId}  model {S(s.ModelRef)}  mode {s.Mode}  status {s.Status}  suite {Short(s.SuiteId)}");
        w.WriteLine("attempts: " + string.Join("  ", s.Outcomes.Select(t => $"{t.Outcome} {t.Count}")));
        if (s.EndToEndRate is { } e2e)
            w.WriteLine($"end-to-end {s.Passed}/{s.Accountable} = {F(e2e)}  (timeouts count as failures)   completion {s.Accountable}/{s.Planned} = {F(s.Completion!.Value)}  (infra errors, cancellations and missing attempts excluded)");
        if (s.PassRate is { } pr)
            w.WriteLine($"graded pass rate {s.Passed}/{s.Graded} = {F(pr)}   mean score {F(s.MeanScore!.Value)}  (graded attempts only)   grading v{s.GradingVersion}");
        if (s.Consistency is { } c) w.WriteLine($"consistency {F(c)}");
        if (s.Excluded > 0) w.WriteLine($"excluded from scores: {s.Excluded}");

        void Slices(string head, IReadOnlyList<Slice> slices)
        {
            if (slices.Count > 0) Table(w, [head, "N", "PASSED", "PASS RATE", "MEAN SCORE"], slices.Select(g => new[] { S(g.Key), g.N.ToString(), g.Passed.ToString(), F(g.PassRate), F(g.MeanScore) }).ToList());
        }
        Slices("FAMILY", s.ByFamily);
        Slices("CATEGORY", s.ByCategory);
        Slices("COMPLEXITY", s.ByComplexity);

        foreach (var name in new[] { MetricNames.CostUsd, MetricNames.InputTokens, MetricNames.OutputTokens, MetricNames.ReasoningTokens, MetricNames.EnergyKwh, MetricNames.EnergyCostUsd })
        {
            var a = s.Metric(name);
            var label = name.StartsWith("energy") ? name + " (Estimated)" : name;
            if (s.Attempts > 0) w.WriteLine($"{label}: {(a.Total is { } t ? ReportCommands.Num(t) : "unavailable")}  ({a.Coverage} attempts)");
        }
        w.WriteLine($"cost per success: {(s.CostPerSuccess is { } cps ? ReportCommands.Num(cps) : "n/a")}");

        if (!showFailures) return;
        foreach (var a in Reports.Failures(db, runId))
        {
            w.WriteLine($"\ntask {a.TaskIndex} {S(a.FamilyId)} c{a.Complexity} repeat {a.Repeat}: {a.Outcome}{(a.Score is { } sc ? $" score {F(sc)}" : "")}");
            if (a.Error is not null) w.WriteLine($"  error: {Truncate(a.Error, 300)}");
            foreach (var r in a.Assertions)
                w.WriteLine($"  FAIL {Truncate(r.Name, 80)}: expected [{Truncate(r.Expected, 200)}] actual [{Truncate(r.Actual, 200)}] {Truncate(r.Diagnostic, 300)}");
        }
    }
}
