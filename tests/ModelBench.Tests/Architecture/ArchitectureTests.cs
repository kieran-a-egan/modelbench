using System.Reflection;
using ModelBench.Core.Providers;

namespace ModelBench.Tests.Architecture;

/// <summary>AC-01/AC-02: graders have no network or provider access, only Providers/ speaks HTTP, generators are deterministic.</summary>
public class ArchitectureTests
{
    private static readonly Assembly Core = typeof(IModelProvider).Assembly;
    private static readonly Type[] Forbidden = [typeof(HttpClient), typeof(HttpMessageHandler), typeof(IModelProvider)];
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static string CoreDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "src", "ModelBench.Core"))) return Path.Combine(d.FullName, "src", "ModelBench.Core");
        throw new DirectoryNotFoundException("src/ModelBench.Core not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Source lines (comments skipped) of the .cs files under a Core subfolder, or of all Core files outside Providers.</summary>
    private static List<(string File, string Line)> Lines(string? sub, bool excludeProviders = false)
    {
        var root = CoreDir();
        var sep = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(sub is null ? root : Path.Combine(root, sub), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}") && !(excludeProviders && f.Contains($"{sep}Providers{sep}"))).ToList();
        Assert.NotEmpty(files);
        return files.SelectMany(f => File.ReadAllLines(f).Where(l => !l.TrimStart().StartsWith("//")).Select(l => (Path.GetFileName(f), l))).ToList();
    }

    private static bool Mentions(Type t, bool httpOnly = false) => Forbidden.Take(httpOnly ? 2 : 3).Any(f => t.IsAssignableTo(f)) || (t.IsGenericType && t.GetGenericArguments().Any(a => Mentions(a, httpOnly))) || (t.HasElementType && Mentions(t.GetElementType()!, httpOnly));

    [Fact] public void GradersHaveNoNetwork()
    {
        var types = Core.GetTypes().Where(t => t.Namespace?.StartsWith("ModelBench.Core.Grading") == true).ToList();
        Assert.True(types.Count > 10);
        var bad = new List<string>();
        foreach (var t in types)
        {
            bad.AddRange(t.GetFields(All).Where(f => Mentions(f.FieldType)).Select(f => $"{t.Name}.{f.Name}"));
            bad.AddRange(t.GetProperties(All).Where(p => Mentions(p.PropertyType)).Select(p => $"{t.Name}.{p.Name}"));
            bad.AddRange(t.GetConstructors(All).SelectMany(c => c.GetParameters()).Where(p => Mentions(p.ParameterType)).Select(p => $"{t.Name} ctor {p.Name}"));
            bad.AddRange(t.GetMethods(All).SelectMany(m => m.GetParameters().Select(p => (m, p))).Where(x => Mentions(x.p.ParameterType)).Select(x => $"{t.Name}.{x.m.Name}({x.p.Name})"));
        }
        Assert.Empty(bad);

        string[] words = ["HttpClient", "IModelProvider", "ProviderFactory", "new Http"];
        Assert.Empty(Lines("Grading").Where(l => words.Any(l.Line.Contains)).Select(l => $"{l.File}: {l.Line.Trim()}"));
    }

    [Fact] public void OnlyProvidersUseHttpClient()
    {
        Assert.Empty(Lines(null, excludeProviders: true).Where(l => l.Line.Contains("HttpClient")).Select(l => $"{l.File}: {l.Line.Trim()}"));
        var outside = Core.GetTypes().Where(t => t.Namespace != "ModelBench.Core.Providers")
            .Where(t => t.GetFields(All).Any(f => Mentions(f.FieldType, true)) || t.GetProperties(All).Any(p => Mentions(p.PropertyType, true))).Select(t => t.FullName).ToList();
        Assert.Empty(outside);
    }

    [Fact] public void GeneratorsAreDeterministic()
    {
        string[] banned = ["System.Random", "new Random(", "Random.Shared", "DateTime.Now", "DateTime.UtcNow", "DateTimeOffset.Now", "DateTimeOffset.UtcNow", "Guid.NewGuid", "Environment.TickCount", "AppendLine("];
        Assert.Empty(Lines("Families").SelectMany(l => banned.Where(l.Line.Contains).Select(b => $"{l.File}: {b}")));
    }
}
