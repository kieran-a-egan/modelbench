using System.ComponentModel;
using System.Diagnostics;
using ModelBench.Core.Grading;

namespace ModelBench.Core.Sandbox;

/// <summary>Image ids and docker server version: part of the run environment fingerprint.</summary>
public sealed record SandboxImageInfo(string PythonImageId, string DotnetImageId, string DockerVersion);

public static class SandboxImages
{
    /// <summary>Walks up from AppContext.BaseDirectory to the repo's sandbox/ directory (or uses the override).</summary>
    public static string FindSandboxDir(string? overrideDir = null)
    {
        if (overrideDir is not null)
            return Directory.Exists(overrideDir) ? overrideDir : throw new InfraException($"Sandbox directory not found: {overrideDir}", false);
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "sandbox");
            if (File.Exists(Path.Combine(candidate, "python", "Dockerfile"))) return candidate;
        }
        throw new InfraException("Could not locate the sandbox/ directory above " + AppContext.BaseDirectory, false);
    }

    public static async Task BuildAsync(string sandboxDir, Action<string> log, CancellationToken ct = default)
    {
        foreach (var (image, dir) in new[] { (DockerSandbox.PythonImage, "python"), (DockerSandbox.DotnetImage, "dotnet") })
        {
            log($"Building {image} ...");
            var (code, stdout, stderr) = await RunDockerAsync(["build", "-t", image, Path.Combine(sandboxDir, dir)], ct);
            if (code != 0) throw new InfraException($"docker build of {image} failed (exit {code}): {stderr}{stdout}", false);
            log($"Built {image}");
        }
    }

    public static async Task<SandboxImageInfo> InspectAsync(CancellationToken ct = default)
    {
        async Task<string> One(params string[] args)
        {
            var (code, stdout, stderr) = await RunDockerAsync(args, ct);
            return code == 0 ? stdout.Trim() : throw new InfraException($"docker {args[0]} failed (exit {code}): {stderr}", false);
        }
        return new SandboxImageInfo(
            await One("image", "inspect", "--format", "{{.Id}}", DockerSandbox.PythonImage),
            await One("image", "inspect", "--format", "{{.Id}}", DockerSandbox.DotnetImage),
            await One("version", "--format", "{{.Server.Version}}"));
    }

    internal static async Task<(int Code, string Stdout, string Stderr)> RunDockerAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process p;
        try { p = Process.Start(psi)!; }
        catch (Win32Exception e) { throw new InfraException("docker executable not found; is Docker installed and on PATH?", false, e); }
        using (p)
        {
            var o = p.StandardOutput.ReadToEndAsync(ct);
            var e = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return (p.ExitCode, await o, await e);
        }
    }
}
