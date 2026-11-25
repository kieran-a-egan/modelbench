namespace ModelBench.Tests.Web;

public class UiSafetyTests
{
    private static string WebRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "src", "ModelBench.Web"))) return Path.Combine(d.FullName, "src", "ModelBench.Web");
        throw new DirectoryNotFoundException("src/ModelBench.Web not found above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> Sources() => Directory.EnumerateFiles(WebRoot(), "*.*", SearchOption.AllDirectories)
        .Where(f => (f.EndsWith(".razor") || f.EndsWith(".cs")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact] public void NoMarkupStringOnUntrusted()
    {
        var files = Sources().ToList();
        Assert.True(files.Count > 10);
        Assert.DoesNotContain(files, f => File.ReadAllText(f).Contains("MarkupString"));
    }

    [Fact] public void NoInnerHtmlInterop() => Assert.DoesNotContain(Sources(), f => File.ReadAllText(f).Contains("innerHTML", StringComparison.OrdinalIgnoreCase));
}
