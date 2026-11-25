using System.CommandLine;
using ModelBench.Cli.Commands;

namespace ModelBench.Cli;

public static class CliApp
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        var cli = new CliContext();
        var root = new RootCommand("ModelBench: deterministic LLM benchmarking");
        root.Options.Add(cli.Db);
        root.Options.Add(cli.Models);
        root.Subcommands.Add(ModelsCommands.Build(cli));
        root.Subcommands.Add(SuiteCommands.Build(cli));
        root.Subcommands.Add(RunCommands.Build(cli));
        root.Subcommands.Add(ManualCommands.Build(cli));
        root.Subcommands.Add(SelfTestCommand.Build(cli));
        var config = new InvocationConfiguration { Output = output, Error = error };
        return await root.Parse(args).InvokeAsync(config, ct);
    }
}
