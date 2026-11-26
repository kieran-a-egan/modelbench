using System.CommandLine;
using System.Diagnostics;
using ModelBench.Core.Config;
using ModelBench.Core.Persistence;
using static ModelBench.Cli.Commands.CliContext;

namespace ModelBench.Cli.Commands;

/// <summary>`modelbench web`: starts the dashboard published next to this executable (install.ps1 puts it there) and opens a browser.</summary>
internal static class WebCommand
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    public static Command Build(CliContext cli)
    {
        var port = new Option<int>("--port") { DefaultValueFactory = _ => 5180, Description = "Port on 127.0.0.1" };
        port.Validators.Add(r => { if (r.GetValueOrDefault<int>() is < 1 or > 65535) r.AddError("--port must be between 1 and 65535"); });
        var noBrowser = new Option<bool>("--no-browser") { Description = "Do not open a browser" };
        var web = new Command("web", "Serve the dashboard on 127.0.0.1 (Ctrl+C stops it)") { port, noBrowser };
        web.SetAction((pr, ct) => Guard(pr, async w =>
        {
            var dir = AppContext.BaseDirectory;
            var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "ModelBench.Web.exe" : "ModelBench.Web");
            if (!File.Exists(exe))
                throw new InvalidOperationException($"the dashboard is not installed next to {dir}. Run install.ps1 from the repository, or use `dotnet run --project src/ModelBench.Web`");

            var url = $"http://127.0.0.1:{pr.GetValue(port)}";
            var nonce = Guid.NewGuid().ToString("N");
            var psi = new ProcessStartInfo(exe, ["--urls", url, "--contentRoot", dir]) { UseShellExecute = false };
            // The same --db / --models resolution as every other command, so the dashboard shows the same data.
            psi.Environment["MODELBENCH_DB"] = DbPaths.Resolve(pr.GetValue(cli.Db));
            if (ModelCatalog.Locate(pr.GetValue(cli.Models)) is { } models) psi.Environment["MODELBENCH_MODELS"] = models;
            psi.Environment["MODELBENCH_LAUNCH_NONCE"] = nonce;

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + exe);
            try
            {
                if (!await WaitReadyAsync(url, nonce, () => proc.HasExited, StartupTimeout, ct))
                    throw new InvalidOperationException(proc.HasExited
                        ? $"the dashboard exited during startup (code {proc.ExitCode}); is {url} already in use?"
                        : $"the dashboard did not answer on {url} within {StartupTimeout.TotalSeconds:0}s");
                w.WriteLine($"dashboard: {url}  (Ctrl+C to stop)");
                if (!pr.GetValue(noBrowser)) Open(url);
                await proc.WaitForExitAsync(ct);
                return proc.ExitCode;
            }
            catch (OperationCanceledException) { return 0; }
            finally { if (!proc.HasExited) { proc.Kill(entireProcessTree: true); proc.WaitForExit(5000); } }
        }));
        return web;
    }

    /// <summary>True once <paramref name="url"/> answers with this launch's nonce; false if the child exits or the timeout passes first.</summary>
    internal static async Task<bool> WaitReadyAsync(string url, string nonce, Func<bool> exited, TimeSpan timeout, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = Stopwatch.StartNew();
        while (!exited() && deadline.Elapsed < timeout)
        {
            try
            {
                using var res = await http.GetAsync(url + "/_modelbench/launch", ct);
                if (res.IsSuccessStatusCode && await res.Content.ReadAsStringAsync(ct) == nonce) return true;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(250, ct);
        }
        return false;
    }

    private static void Open(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else Process.Start(OperatingSystem.IsMacOS() ? "open" : "xdg-open", url);
        }
        catch (System.ComponentModel.Win32Exception) { } // no browser available: the URL is printed above
    }
}
