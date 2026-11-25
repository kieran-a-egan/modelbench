using System.CommandLine;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ModelBench.Core.Families;
using ModelBench.Core.Generation;
using static ModelBench.Cli.Commands.CliContext;

namespace ModelBench.Cli.Commands;

internal static class SuiteCommands
{
    public static Command Build(CliContext cli)
    {
        var suite = new Command("suite", "Generate, inspect and verify frozen suites");

        var name = new Option<string>("--name") { Required = true, Description = "Suite name" };
        var seed = new Option<long>("--seed") { Required = true, Description = "Master seed" };
        var family = new Option<string[]>("--family") { Required = true, Description = "id[:complexity[:count]] (complexity 1..5 or 'all'); repeatable" };
        var generate = new Command("generate", "Generate and persist a suite") { name, seed, family };
        generate.SetAction((pr, ct) => Guard(pr, w =>
        {
            var spec = new SuiteSpec(pr.GetRequiredValue(name), pr.GetRequiredValue(seed),
                pr.GetRequiredValue(family).SelectMany(ParseFamily).ToList());
            var generated = SuiteGenerator.Generate(spec);
            using var db = cli.OpenDb(pr);
            SuiteGenerator.Persist(db, generated);
            w.WriteLine($"suite {generated.Id} ({generated.Tasks.Count} tasks, hash {generated.ContentHash[..12]})");
            return Task.FromResult(0);
        }));

        var list = new Command("list", "List suites");
        list.SetAction((pr, ct) => Guard(pr, w =>
        {
            using var db = cli.OpenDb(pr);
            var rows = db.Suites.AsNoTracking().Select(s => new { s.Id, s.Name, s.MasterSeed, s.ContentHash, s.CreatedAt, Tasks = s.Tasks.Count }).ToList()
                .OrderBy(s => s.CreatedAt)
                .Select(s => new[] { Short(s.Id), S(s.Name), s.Tasks.ToString(), s.MasterSeed.ToString(CultureInfo.InvariantCulture), s.ContentHash[..12], s.CreatedAt.ToString("u") })
                .ToList();
            Table(w, ["ID", "NAME", "TASKS", "SEED", "HASH", "CREATED"], rows);
            return Task.FromResult(0);
        }));

        var showId = new Argument<string>("id") { Description = "Suite id or unique prefix" };
        var show = new Command("show", "Show a suite's tasks") { showId };
        show.SetAction((pr, ct) => Guard(pr, w =>
        {
            using var db = cli.OpenDb(pr);
            var id = ResolveId(pr.GetRequiredValue(showId), db.Suites.Select(s => s.Id).ToList(), "suite");
            var s = db.Suites.AsNoTracking().Single(x => x.Id == id);
            w.WriteLine($"suite {s.Id}  {S(s.Name)}  seed {s.MasterSeed}  hash {s.ContentHash}");
            var rows = db.Tasks.AsNoTracking().Where(t => t.SuiteId == id).OrderBy(t => t.Index).ToList()
                .Select(t => new[] { t.Index.ToString(), t.FamilyId, t.Complexity.ToString(), t.Mode.ToString(), t.Seed.ToString(CultureInfo.InvariantCulture), t.ContentHash[..12] })
                .ToList();
            Table(w, ["#", "FAMILY", "CX", "MODE", "SEED", "HASH"], rows);
            return Task.FromResult(0);
        }));

        var verifyId = new Argument<string>("id") { Description = "Suite id or unique prefix" };
        var verify = new Command("verify", "Regenerate a suite and compare every hash") { verifyId };
        verify.SetAction((pr, ct) => Guard(pr, w =>
        {
            using var db = cli.OpenDb(pr);
            var id = ResolveId(pr.GetRequiredValue(verifyId), db.Suites.Select(s => s.Id).ToList(), "suite");
            var problems = SuiteGenerator.Verify(db, id);
            if (problems.Count == 0) w.WriteLine("verified: every task regenerates to its stored hash");
            else
            {
                w.WriteLine($"VERIFICATION FAILED ({problems.Count} problems)");
                foreach (var p in problems) w.WriteLine("  " + S(p));
            }
            return Task.FromResult(problems.Count == 0 ? 0 : 1);
        }));

        suite.Subcommands.Add(generate);
        suite.Subcommands.Add(list);
        suite.Subcommands.Add(show);
        suite.Subcommands.Add(verify);
        return suite;
    }

    /// <summary>"id[:complexity[:count]]". The id is the longest registered family id that prefixes the text.</summary>
    internal static IEnumerable<FamilySpec> ParseFamily(string text)
    {
        var fam = FamilyRegistry.All.Where(f => text == f.Id || text.StartsWith(f.Id + ":", StringComparison.Ordinal)).MaxBy(f => f.Id.Length)
            ?? throw new ArgumentException($"unknown family in '{text}' (known: {string.Join(", ", FamilyRegistry.All.Select(f => f.Id))})");
        var parts = text[fam.Id.Length..].Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 2) throw new ArgumentException($"'{text}': expected id[:complexity[:count]]");
        var count = parts.Length == 2 ? ParseInt(parts[1], "count") : 5;
        if (parts.Length == 0 || parts[0] == "all")
            return Enumerable.Range(Complexity.Min, Complexity.Max).Select(c => new FamilySpec(fam.Id, c, count));
        return [new FamilySpec(fam.Id, ParseInt(parts[0], "complexity"), count)];
    }

    private static int ParseInt(string s, string what)
        => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : throw new ArgumentException($"{what} '{s}' is not a number");
}
