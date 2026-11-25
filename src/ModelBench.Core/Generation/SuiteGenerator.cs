using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ModelBench.Core.Domain;
using ModelBench.Core.Families;
using ModelBench.Core.Persistence;

namespace ModelBench.Core.Generation;


public static class SuiteGenerator
{
    /// <summary>Generates and validates every task in memory. Throws if any task fails Validate.</summary>
    public static Suite Generate(SuiteSpec spec)
    {
        var tasks = new List<TaskInstance>();
        var recorded = new List<FamilySpec>();
        var suiteId = Guid.NewGuid();
        var index = 0;
        foreach (var fs in spec.Families)
        {
            var fam = FamilyRegistry.Get(fs.Family);
            if (fs.Complexity is < Complexity.Min or > Complexity.Max) throw new ArgumentException($"complexity {fs.Complexity} outside 1..5");
            if (fs.Count < 1) throw new ArgumentException($"count for {fs.Family} must be >= 1");
            recorded.Add(fs with { Version = fam.Version });
            for (var i = 0; i < fs.Count; i++, index++)
            {
                var seed = Rng.DeriveSeed(spec.MasterSeed, fam.Id, fam.Version, index);
                var generated = fam.Generate(new Rng(seed), fs.Complexity);
                var check = fam.Validate(generated);
                if (!check.Ok)
                    throw new InvalidOperationException($"{fam.Id} complexity {fs.Complexity} seed {seed} failed validation: {string.Join("; ", check.Problems)}");
                var prompt = JsonUtil.Serialize(generated.Prompt);
                var hidden = JsonUtil.Serialize(generated.Hidden);
                tasks.Add(new TaskInstance
                {
                    SuiteId = suiteId, Index = index, FamilyId = fam.Id, FamilyVersion = fam.Version, Category = fam.Category,
                    Complexity = fs.Complexity, Seed = seed, Mode = fam.Mode, PromptJson = prompt, HiddenJson = hidden,
                    ContentHash = TaskHash(prompt, hidden),
                });
            }
        }
        return new Suite
        {
            Id = suiteId, Name = spec.Name, MasterSeed = spec.MasterSeed,
            SpecJson = JsonUtil.Serialize(spec with { Families = recorded }),
            ContentHash = SuiteHash(tasks.Select(t => t.ContentHash)),
            Tasks = tasks,
        };
    }

    public static void Persist(BenchDb db, Suite suite)
    {
        db.Suites.Add(suite);
        db.SaveChanges();
    }

    /// <summary>Regenerates the suite from its stored spec and compares every hash. Empty list = verified.</summary>
    public static List<string> Verify(BenchDb db, Guid suiteId)
    {
        var suite = db.Suites.Single(s => s.Id == suiteId);
        var stored = db.Tasks.Where(t => t.SuiteId == suiteId).OrderBy(t => t.Index).ToList();
        var spec = JsonUtil.Deserialize<SuiteSpec>(suite.SpecJson);
        var problems = new List<string>();

        foreach (var fs in spec.Families)
        {
            var fam = FamilyRegistry.Find(fs.Family);
            if (fam is null) problems.Add($"family '{fs.Family}' is not registered");
            else if (fam.Version != fs.Version) problems.Add($"family '{fs.Family}' is now version {fam.Version}; suite was generated with {fs.Version}");
        }
        if (problems.Count > 0) return problems;

        foreach (var t in stored)
            if (TaskHash(t.PromptJson, t.HiddenJson) != t.ContentHash)
                problems.Add($"task {t.Index} ({t.FamilyId}): stored prompt/hidden do not match the stored hash (modified)");
        if (SuiteHash(stored.Select(t => t.ContentHash)) != suite.ContentHash)
            problems.Add("suite hash does not match its stored task hashes");

        var fresh = Generate(spec);
        if (fresh.Tasks.Count != stored.Count) problems.Add($"task count: stored {stored.Count}, regenerated {fresh.Tasks.Count}");
        foreach (var (f, s) in fresh.Tasks.Zip(stored))
            if (f.ContentHash != s.ContentHash)
                problems.Add($"task {s.Index} ({s.FamilyId}): regenerated hash {f.ContentHash[..12]} differs from stored {s.ContentHash[..12]}");
        if (fresh.ContentHash != suite.ContentHash) problems.Add("regenerated suite hash differs from the stored suite hash");
        return problems;
    }

    public static string TaskHash(string promptJson, string hiddenJson) => Sha256Hex(promptJson + "\n" + hiddenJson);

    public static string SuiteHash(IEnumerable<string> taskHashes) => Sha256Hex(string.Join("\n", taskHashes));

    private static string Sha256Hex(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
