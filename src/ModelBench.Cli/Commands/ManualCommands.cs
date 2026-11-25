using System.CommandLine;
using ModelBench.Core.Domain;
using ModelBench.Core.Execution;
using ModelBench.Core.Sandbox;
using static ModelBench.Cli.Commands.CliContext;

namespace ModelBench.Cli.Commands;

internal static class ManualCommands
{
    public static Command Build(CliContext cli)
    {
        var manual = new Command("manual", "Export prompts to a chat UI and import the pasted responses");

        var suiteOpt = new Option<string>("--suite") { Required = true, Description = "Suite id or unique prefix" };
        var modelOpt = new Option<string>("--model") { Required = true, Description = "provider/model (an api:manual entry)" };
        var outOpt = new Option<string>("--out") { Required = true, Description = "Output directory for prompts.jsonl and prompts.md" };
        var repeatsOpt = new Option<int>("--repeats") { DefaultValueFactory = _ => 1, Description = "Responses per task" };
        var export = new Command("export", "Create a Manual run and write its prompts") { suiteOpt, modelOpt, outOpt, repeatsOpt };
        export.SetAction((pr, ct) => Guard(pr, w =>
        {
            using var db = cli.OpenDb(pr);
            var suiteId = ResolveId(pr.GetRequiredValue(suiteOpt), db.Suites.Select(s => s.Id).ToList(), "suite");
            var model = cli.Catalog(pr).Resolve(pr.GetRequiredValue(modelOpt));
            var run = RunService.CreateRun(db, suiteId, model, ExecutionMode.Manual, pr.GetRequiredValue(repeatsOpt), 300, new RetryPolicy(), "manual");
            var n = ManualExchange.Export(db, run.Id, pr.GetRequiredValue(outOpt));
            w.WriteLine($"run {run.Id}");
            w.WriteLine($"wrote {n} prompts to {pr.GetRequiredValue(outOpt)}");
            return Task.FromResult(0);
        }));

        var runOpt = new Option<string>("--run") { Required = true, Description = "Manual run id or unique prefix" };
        var fileOpt = new Option<string>("--file") { Required = true, Description = "responses.jsonl" };
        var import = new Command("import", "Grade pasted responses") { runOpt, fileOpt };
        import.SetAction((pr, ct) => Guard(pr, async w =>
        {
            using var db = cli.OpenDb(pr);
            var runId = ResolveId(pr.GetRequiredValue(runOpt), db.Runs.Select(r => r.Id).ToList(), "run");
            var report = await ManualExchange.ImportAsync(db, runId, pr.GetRequiredValue(fileOpt), new UnavailableSandbox(), ct);
            w.WriteLine($"graded {report.Graded} responses");
            foreach (var e in report.Errors) w.WriteLine("  skipped: " + S(e));
            if (report.Graded > 0) RunCommands.Report(db, runId, false, w);
            return report.Errors.Count == 0 ? 0 : 1;
        }));

        manual.Subcommands.Add(export);
        manual.Subcommands.Add(import);
        return manual;
    }
}
