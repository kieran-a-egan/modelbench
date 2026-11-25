using System.CommandLine;
using ModelBench.Core.Sandbox;
using static ModelBench.Cli.Commands.CliContext;

namespace ModelBench.Cli.Commands;

internal static class SandboxCommands
{
    public static Command Build()
    {
        var sandbox = new Command("sandbox", "Manage the code-execution sandbox images");

        var dir = new Option<string?>("--dir") { Description = "Sandbox directory (default: found above the executable)" };
        var build = new Command("build", "Build the python and dotnet sandbox images") { dir };
        build.SetAction((pr, ct) => Guard(pr, async w =>
        {
            await SandboxImages.BuildAsync(SandboxImages.FindSandboxDir(pr.GetValue(dir)), w.WriteLine, ct);
            return 0;
        }));

        var check = new Command("check", "Print sandbox image ids and the docker server version");
        check.SetAction((pr, ct) => Guard(pr, async w =>
        {
            var i = await SandboxImages.InspectAsync(ct);
            w.WriteLine($"{DockerSandbox.PythonImage}  {i.PythonImageId}");
            w.WriteLine($"{DockerSandbox.DotnetImage}  {i.DotnetImageId}");
            w.WriteLine($"docker server {i.DockerVersion}");
            return 0;
        }));

        sandbox.Subcommands.Add(build);
        sandbox.Subcommands.Add(check);
        return sandbox;
    }
}
