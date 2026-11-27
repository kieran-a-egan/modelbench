using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ModelBench.Core.Generation;
using ModelBench.Core.Config;
using ModelBench.Core.Domain;
using ModelBench.Core.Grading;
using ModelBench.Core.Persistence;
using ModelBench.Core.Sandbox;

namespace ModelBench.Core.Execution;


public static class RunService
{
    /// <summary>Creates a run for a persisted suite (AC-04), snapshotting the model (no secrets), retry policy and environment.</summary>
    public static Run CreateRun(BenchDb db, Guid suiteId, ResolvedModel model, ExecutionMode mode, int repeats, int timeoutSeconds, RetryPolicy retryPolicy, string? label, int concurrency = 1)
    {
        if (concurrency < 1) throw new ArgumentException("concurrency must be >= 1");
        if (repeats < 1) throw new ArgumentException("repeats must be >= 1");
        if (timeoutSeconds < 1) throw new ArgumentException("timeout must be >= 1 second");
        if (!db.Suites.Any(s => s.Id == suiteId)) throw new InvalidOperationException($"suite {suiteId} is not persisted");
        var taskMode = mode == ExecutionMode.Manual ? ExecutionMode.SingleTurn : mode;
        if (!db.Tasks.Any(t => t.SuiteId == suiteId && t.Mode == taskMode))
            throw new InvalidOperationException($"suite has no {taskMode} tasks, so a {mode} run would be empty");

        var run = new Run
        {
            SuiteId = suiteId, ModelRef = model.Ref, ModelSnapshotJson = ModelCatalog.Snapshot(model), Mode = mode,
            Repeats = repeats, Concurrency = concurrency, TimeoutSeconds = timeoutSeconds, RetryPolicyJson = JsonUtil.Serialize(retryPolicy),
            EnvironmentJson = EnvironmentJson(), Status = RunStatus.Pending, Label = label,
        };
        db.Runs.Add(run);
        db.SaveChanges();
        return run;
    }

    /// <summary>
    /// Validates that a run can be resumed and returns the model re-resolved from the catalog. Refuses when the suite is missing or
    /// changed, or the model snapshot differs from the stored one: every attempt of a run must share one configuration (MB-005).
    /// </summary>
    public static ResolvedModel PrepareResume(BenchDb db, Guid runId, ModelCatalog catalog)
    {
        var run = db.Runs.SingleOrDefault(r => r.Id == runId) ?? throw new InvalidOperationException($"run {runId} not found");
        if (run.Mode == ExecutionMode.Manual) throw new InvalidOperationException("manual runs are completed with 'manual import'");
        RequireCurrentGradingVersion(run);
        // Early, friendly refusal; RunExecutor's atomic lease acquisition is what actually prevents a double execution.
        if (run.LeaseOwner is not null && run.LeaseExpiresMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new RunLeaseException($"run {runId} is being executed by another process; wait for it, or cancel it with 'run cancel'");
        var suite = db.Suites.SingleOrDefault(s => s.Id == run.SuiteId) ?? throw new InvalidOperationException($"suite {run.SuiteId} no longer exists");
        var tasks = db.Tasks.Where(t => t.SuiteId == suite.Id).OrderBy(t => t.Index).ToList();
        if (tasks.Any(t => SuiteGenerator.TaskHash(t.PromptJson, t.HiddenJson) != t.ContentHash)
            || SuiteGenerator.SuiteHash(tasks.Select(t => t.ContentHash)) != suite.ContentHash)
            throw new InvalidOperationException($"suite {suite.Id} no longer matches its content hash; refusing to resume");

        var model = catalog.Resolve(run.ModelRef);
        var diff = SnapshotDiff(run.ModelSnapshotJson, ModelCatalog.Snapshot(model));
        if (diff.Count > 0)
            throw new InvalidOperationException($"model configuration for {run.ModelRef} changed since the run started:\n  " + string.Join("\n  ", diff)
                + "\nrevert the configuration to resume, or start a new run with the new configuration");
        return model;
    }

    /// <summary>Resuming or importing into a run graded with another methodology would mix versions in one run.</summary>
    public static void RequireCurrentGradingVersion(Run run)
    {
        if (run.GradingVersion != GradingPipeline.Version)
            throw new InvalidOperationException($"run {run.Id} was graded with methodology v{run.GradingVersion}; this build grades with v{GradingPipeline.Version}, so resuming would mix them. Start a new run.");
    }

    private static List<string> SnapshotDiff(string stored, string fresh)
    {
        var a = JsonNode.Parse(stored)!.AsObject();
        var b = JsonNode.Parse(fresh)!.AsObject();
        static string V(JsonNode? n) => n?.ToJsonString() ?? "(absent)";
        return a.Select(kv => kv.Key).Union(b.Select(kv => kv.Key)).Order()
            .Where(k => V(a[k]) != V(b[k]))
            .Select(k => $"{k}: {V(a[k])} -> {V(b[k])}").ToList();
    }

    /// <summary>Asks the process executing the run (this or another one) to stop; it polls the flag.</summary>
    public static void RequestCancel(BenchDb db, Guid runId)
    {
        var run = db.Runs.SingleOrDefault(r => r.Id == runId) ?? throw new InvalidOperationException($"run {runId} not found");
        if (run.Status is not (RunStatus.Pending or RunStatus.Running)) throw new InvalidOperationException($"run is {run.Status}, nothing to cancel");
        run.CancelRequested = true;
        db.SaveChanges();
    }

    public static string EnvironmentJson() => JsonUtil.Serialize(new
    {
        processorCount = Environment.ProcessorCount,
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
