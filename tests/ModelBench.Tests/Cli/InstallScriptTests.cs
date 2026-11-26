using System.Diagnostics;
using ModelBench.Tests.Support;

namespace ModelBench.Tests.Cli;

/// <summary>
/// install.ps1 deletes $Dest/benchmarks and $Dest/sandbox; a -Dest that overlaps the repository, directly or through a
/// symlink/junction, must fail before anything is published or deleted. Runs the real script in every PowerShell available.
/// </summary>
public class InstallScriptTests
{
    private static string RealScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "install.ps1"))) dir = dir.Parent ?? throw new FileNotFoundException("install.ps1");
        return Path.Combine(dir.FullName, "install.ps1");
    }

    private static bool Available(string shell)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(shell, ["-NoProfile", "-Command", "exit 0"]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>Removes the links before the temp dir: a recursive delete reaching a junction fails with access denied.</summary>
    private sealed class Links(TempDir tmp) : IDisposable
    {
        private readonly List<string> _made = [];
        public void Add(string link, string target) { Link(link, target); _made.Add(link); }
        public void Dispose() { foreach (var l in _made) Directory.Delete(l); tmp.Dispose(); }
    }

    /// <summary>A directory junction on Windows (no privileges needed), a symlink elsewhere.</summary>
    private static void Link(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        using var p = Process.Start(new ProcessStartInfo("cmd", ["/c", "mklink", "/J", link, target]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    public static TheoryData<string, string, string> Cases()
    {
        string[] dests =
        [
            "{repo}|",                  // the repository itself
            ".|{repo}",                 // relative, from inside it
            "{repo}/sub/..|",           // an equivalent path
            "{repo}/benchmarks/x|",     // inside a source folder
            "{root}|",                  // the repo lives at {root}/benchmarks/repo, so {root}/benchmarks must not be replaced
            "{tmp}/alias|",             // a link to the repository
            "{tmp}/alias/sub|",         // inside the repository through a linked ancestor
            "{tmp}/rootalias|",         // a link to {root}
            "{tmp}/out|",               // {tmp}/out/benchmarks is a link to the repo's benchmarks folder
        ];
        var data = new TheoryData<string, string, string>();
        foreach (var shell in OperatingSystem.IsWindows() ? new[] { "pwsh", "powershell" } : ["pwsh"])
            foreach (var d in dests) data.Add(shell, d.Split('|')[0], d.Split('|')[1]);
        return data;
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task OverlappingDestFailsWithoutChangingTheRepository(string shell, string dest, string cwd)
    {
        Assert.SkipUnless(Available(shell), $"{shell} is not installed");
        var tmp = new TempDir();
        using var links = new Links(tmp);
        var root = tmp.File("root");
        var repo = Path.Combine(root, "benchmarks", "repo");
        foreach (var d in new[] { "benchmarks", "sandbox", "src" })
        {
            Directory.CreateDirectory(Path.Combine(repo, d));
            File.WriteAllText(Path.Combine(repo, d, "keep.txt"), d);
        }
        File.Copy(RealScript(), Path.Combine(repo, "install.ps1"));
        links.Add(tmp.File("alias"), repo);
        links.Add(tmp.File("rootalias"), root);
        Directory.CreateDirectory(tmp.File("out"));
        links.Add(Path.Combine(tmp.File("out"), "benchmarks"), Path.Combine(repo, "benchmarks"));
        string Sub(string s) => s.Replace("{repo}", repo).Replace("{root}", root).Replace("{tmp}", tmp.Path).Replace('/', Path.DirectorySeparatorChar);

        var psi = new ProcessStartInfo(shell, ["-NoProfile", "-NonInteractive", "-File", Path.Combine(repo, "install.ps1"), "-Dest", Sub(dest)])
            { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd == "" ? tmp.Path : Sub(cwd) };
        using var p = Process.Start(psi)!;
        var ct = TestContext.Current.CancellationToken;
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var error = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);

        Assert.NotEqual(0, p.ExitCode);
        Assert.Contains("choose", error + await output); // the guard's message, not a later publish failure
        foreach (var d in new[] { "benchmarks", "sandbox", "src" })
            Assert.Equal(d, File.ReadAllText(Path.Combine(repo, d, "keep.txt")));
        Assert.Equal(["benchmarks", "sandbox", "src"], Directory.GetDirectories(repo).Select(Path.GetFileName).Order());
    }

    [Fact] public async Task ALinkedPathOutsideTheRepositoryPassesTheGuard()
    {
        Assert.SkipUnless(Available("pwsh"), "pwsh is not installed");
        var tmp = new TempDir();
        using var links = new Links(tmp);
        var repo = tmp.File("repo");
        Directory.CreateDirectory(Path.Combine(repo, "benchmarks"));
        File.Copy(RealScript(), Path.Combine(repo, "install.ps1"));
        Directory.CreateDirectory(tmp.File("elsewhere"));
        links.Add(tmp.File("alias"), tmp.File("elsewhere"));

        var psi = new ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-File", Path.Combine(repo, "install.ps1"), "-Dest", tmp.File("alias")])
            { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var ct = TestContext.Current.CancellationToken;
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var text = await p.StandardError.ReadToEndAsync(ct) + await output;
        await p.WaitForExitAsync(ct);
        Assert.DoesNotContain("choose", text); // got past the guard; publish then fails because the fake repo has no projects
    }
}
