using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ModelBench.Core.Config;
using ModelBench.Core.Domain;
using ModelBench.Core.Grading;
using ModelBench.Core.Persistence;
using ModelBench.Core.Sandbox;

namespace ModelBench.Core.Execution;


public static class RunService
{
    /// <summary>Creates a run for a persisted suite (AC-04), snapshotting the model (no secrets), retry policy and environment.</summary>
    public static Run CreateRun(BenchDb db, Guid suiteId, ResolvedModel model, ExecutionMode mode, int repeats, int timeoutSeconds, RetryPolicy retryPolicy, string? label)
    {
        if (repeats < 1) throw new ArgumentException("repeats must be >= 1");
        if (timeoutSeconds < 1) throw new ArgumentException("timeout must be >= 1 second");
        if (!db.Suites.Any(s => s.Id == suiteId)) throw new InvalidOperationException($"suite {suiteId} is not persisted");
        var taskMode = mode == ExecutionMode.Manual ? ExecutionMode.SingleTurn : mode;
        if (!db.Tasks.Any(t => t.SuiteId == suiteId && t.Mode == taskMode))
            throw new InvalidOperationException($"suite has no {taskMode} tasks, so a {mode} run would be empty");

        var run = new Run
        {
            SuiteId = suiteId, ModelRef = model.Ref, ModelSnapshotJson = ModelCatalog.Snapshot(model), Mode = mode,
            Repeats = repeats, TimeoutSeconds = timeoutSeconds, RetryPolicyJson = JsonUtil.Serialize(retryPolicy),
            EnvironmentJson = EnvironmentJson(), Status = RunStatus.Pending, Label = label,
        };
        db.Runs.Add(run);
        db.SaveChanges();
        return run;
    }

    public static string EnvironmentJson() => JsonUtil.Serialize(new
    {
        os = RuntimeInformation.OSDescription,
        dotnet = RuntimeInformation.FrameworkDescription,
        machine = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName)))[..12],
        appVersion = typeof(RunService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
        gitSha = GitSha(),
        sandbox = SandboxFingerprint(),
    });

    /// <summary>Image ids and docker server version, or "unavailable" when docker cannot be queried. Never guessed.</summary>
    private static object SandboxFingerprint()
    {
        try
        {
            var i = Task.Run(() => SandboxImages.InspectAsync()).GetAwaiter().GetResult();
            return new { pythonImageId = i.PythonImageId, dotnetImageId = i.DotnetImageId, dockerVersion = i.DockerVersion };
        }
        catch (InfraException) { return "unavailable"; }
    }

    private static string GitSha()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = AppContext.BaseDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            })!;
            var sha = p.StandardOutput.ReadToEnd().Trim();
            return p.WaitForExit(5000) && p.ExitCode == 0 && sha.Length > 0 ? sha : "unknown";
        }
        catch (System.ComponentModel.Win32Exception) { return "unknown"; } // git is not installed
    }
}
