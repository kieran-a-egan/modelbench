using System.CommandLine;
using ModelBench.Core.Execution;
using ModelBench.Core.Sandbox;
using static ModelBench.Cli.Commands.CliContext;

namespace ModelBench.Cli.Commands;

internal static class SelfTestCommand
{
    public static Command Build(CliContext cli)
    {
        var family = new Option<string?>("--family") { Description = "Only this family id" };
        var cmd = new Command("selftest", "Replay every family's reference and known-bad answers through the real pipeline") { family };
        cmd.SetAction((pr, ct) => Guard(pr, async w =>
        {
            var report = await SelfTest.RunAsync(pr.GetValue(family), 3, new UnavailableSandbox(), ct);
            foreach (var f in report.Failures)
                w.WriteLine($"FAIL {f.Family} c{f.Complexity} #{f.Sample} {f.Case}: {S(f.Problem)}");
            if (report.Skipped > 0) w.WriteLine($"skipped {report.Skipped} generated tasks that have no reference answer (grading not exercised)");
            w.WriteLine(report.Ok ? $"selftest passed ({report.Checks} checks)" : $"selftest FAILED: {report.Failures.Count} of {report.Checks} checks");
            return report.Ok ? 0 : 1;
        }));
        return cmd;
    }
}
