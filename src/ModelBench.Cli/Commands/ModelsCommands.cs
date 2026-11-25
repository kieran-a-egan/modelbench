using System.CommandLine;
using static ModelBench.Cli.Commands.CliContext;

namespace ModelBench.Cli.Commands;

internal static class ModelsCommands
{
    public static Command Build(CliContext cli)
    {
        var list = new Command("list", "List models from models.json");
        list.SetAction((pr, ct) => Guard(pr, w =>
        {
            var rows = cli.Catalog(pr).List()
                .Select(e => new[] { S(e.Ref), e.Api.ToString(), e.Reasoning ? "yes" : "no", e.ContextWindow?.ToString() ?? "-", e.Variants.Count == 0 ? "-" : S(string.Join(",", e.Variants)) })
                .ToList();
            Table(w, ["REF", "API", "REASONING", "CONTEXT", "VARIANTS"], rows);
            return Task.FromResult(0);
        }));

        var models = new Command("models", "Configured models");
        models.Subcommands.Add(list);
        return models;
    }
}
