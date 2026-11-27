using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelBench.Core.Grading;

namespace ModelBench.Core.Sandbox;

/// <summary>Runs candidate code in a locked-down container per request (ADR 0003).</summary>
public sealed class DockerSandbox : ISandbox
{
    public const string PythonImage = "modelbench-python:1";
    public const string DotnetImage = "modelbench-dotnet:1";
    internal const int OutputCap = 1 << 20;
    private const int GraceMs = 5_000;

    /// <summary>The only fields that ever enter the container (ADR 0002).</summary>
    public sealed record WireRequest(string Action, string Code, string EntryPoint, IReadOnlyList<JsonElement> Inputs, int Repetitions, string Nonce);

    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string BuildRequestJson(SandboxRequest r, string nonce) =>
        JsonSerializer.Serialize(new WireRequest(r.Action.ToString().ToLowerInvariant(), r.Code, r.EntryPoint, r.Inputs, r.Repetitions, nonce), Wire);

    /// <summary>
    /// Run: TimeoutMs per call, for every repetition of every input. Compile/Inspect: TimeoutMs once.
    /// Plus 5 s grace for container start-up. Candidates are killed by the host at this limit, never trusted to self-limit.
    /// </summary>
    public static long WallClockMs(SandboxRequest r)
    {
        long calls = r.Action == SandboxAction.Run ? (long)Math.Max(1, r.Repetitions) * Math.Max(1, r.Inputs.Count) : 1;
        return (long)r.TimeoutMs * calls + GraceMs;
    }

    public async Task<SandboxResult> ExecuteAsync(SandboxRequest request, CancellationToken ct)
    {
        var name = "mb-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var image = request.Language == SandboxLanguage.Python ? PythonImage : DotnetImage;

        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "run", "--rm", "-i", "--pull", "never", "--name", name, "--network", "none", "--read-only",
                     "--tmpfs", "/tmp:rw,size=64m", "--memory", "256m", "--memory-swap", "256m", "--cpus", "1",
                     "--pids-limit", "64", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                     "--user", "65534:65534", "-e", "HOME=/tmp" })
            psi.ArgumentList.Add(a);
        if (request.Language == SandboxLanguage.CSharp)
            foreach (var a in new[] { "-e", "DOTNET_EnableDiagnostics=0", "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1" })
                psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(image);

        Process process;
        try { process = Process.Start(psi)!; }
        catch (Win32Exception e) { throw new InfraException("docker executable not found; is Docker installed and on PATH?", false, e); }

        using (process)
        {
            var stdout = Capture(process.StandardOutput.BaseStream);
            var stderr = Capture(process.StandardError.BaseStream);
            try
            {
                await process.StandardInput.WriteAsync(BuildRequestJson(request, nonce));
                process.StandardInput.Close();
            }
            catch (IOException) { /* container exited before reading stdin; handled via exit code */ }

            var timedOut = false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(WallClockMs(request)));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                timedOut = !ct.IsCancellationRequested;
                await Kill(name);
                try { process.Kill(true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
                ct.ThrowIfCancellationRequested();
            }
            var (outBytes, errBytes) = (await stdout, await stderr);
            var errText = Encoding.UTF8.GetString(errBytes);
            var tail = errText.Length > 2000 ? errText[^2000..] : errText;
            var exit = process.ExitCode;

            if (!timedOut && exit == 125)
                throw new InfraException(errText.Contains("No such image") || errText.Contains("Unable to find image") || errText.Contains("pull access denied")
                    ? $"Sandbox image {image} is missing. Run `modelbench sandbox build`."
                    : $"docker run failed (exit 125): {tail}", false);

            var result = timedOut ? null : ParseFrame(Encoding.UTF8.GetString(outBytes), nonce);
            // No valid frame: the candidate crashed, was OOM-killed (137) or timed out. That is a model failure.
            return result is null
                ? new SandboxResult(false, null, [], null, timedOut, exit, tail)
                : result with { ExitCode = exit, StdErrTail = tail };
        }
    }

    private static async Task Kill(string name)
    {
        try { await SandboxImages.RunDockerAsync(["kill", name], CancellationToken.None); }
        catch (InfraException) { /* docker gone; the process kill that follows is best effort */ }
    }

    /// <summary>Keeps the first OutputCap bytes, discards the rest but keeps draining.</summary>
    private static Task<byte[]> Capture(Stream s) => Task.Run(async () =>
    {
        var buf = new byte[8192];
        using var kept = new MemoryStream();
        int n;
        while ((n = await s.ReadAsync(buf)) > 0)
            if (kept.Length < OutputCap) kept.Write(buf, 0, (int)Math.Min(n, OutputCap - kept.Length));
        return kept.ToArray();
    });

    internal static SandboxResult? ParseFrame(string stdout, string nonce)
    {
        var prefix = $"MBRESULT:{nonce}:";
        // The runner writes exactly one frame. More than one means candidate code learned the nonce and wrote its own (MB-004): unusable.
        var frames = stdout.Split('\n').Where(l => l.StartsWith(prefix, StringComparison.Ordinal)).Take(2).ToList();
        if (frames.Count != 1) return null;
        var line = frames[0];
        try
        {
            using var doc = JsonDocument.Parse(Convert.FromBase64String(line[prefix.Length..].Trim()));
            var root = doc.RootElement;
            var cases = root.GetProperty("cases").EnumerateArray().Select(c => new CaseResult(
                c.GetProperty("index").GetInt32(),
                c.GetProperty("output") is { ValueKind: not JsonValueKind.Null } o ? o.Clone() : null,
                c.GetProperty("error").GetString(),
                c.GetProperty("elapsedMs").GetDouble())).ToList();
            var facts = root.GetProperty("astFacts");
            return new SandboxResult(root.GetProperty("compiled").GetBoolean(), root.GetProperty("compileErrors").GetString(), cases,
                facts.ValueKind == JsonValueKind.Null ? null : facts.Clone(), false, 0, "");
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
