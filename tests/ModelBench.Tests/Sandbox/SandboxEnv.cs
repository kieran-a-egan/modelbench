using System.Text.Json;
using ModelBench.Core.Grading;
using ModelBench.Core.Sandbox;

namespace ModelBench.Tests.Sandbox;

/// <summary>Builds both images once per test run (docker layer cache keeps this fast) and skips tests when docker is unavailable.</summary>
internal static class SandboxEnv
{
    private static readonly Lazy<Task<string?>> Ready = new(async () =>
    {
        try { await SandboxImages.BuildAsync(SandboxImages.FindSandboxDir(), _ => { }); return null; }
        catch (InfraException e) { return e.Message; }
    });

    public static readonly DockerSandbox Sandbox = new();

    /// <summary>The shared sandbox once the images are built; skips the test when docker is unavailable.</summary>
    public static async Task<DockerSandbox> Require()
    {
        if (await Ready.Value is { } skip) Assert.Skip("docker unavailable: " + skip);
        return Sandbox;
    }

    public static async Task<SandboxResult> Run(SandboxLanguage lang, string code, string entry, int timeoutMs = 10_000,
        SandboxAction action = SandboxAction.Run, int repetitions = 1, params string[] inputs)
    {
        var skip = await Ready.Value;
        if (skip is not null) Assert.Skip("docker unavailable: " + skip);
        var req = new SandboxRequest(lang, action, code, entry, inputs.Select(i => JsonDocument.Parse(i).RootElement.Clone()).ToList(), timeoutMs, repetitions);
        return await Sandbox.ExecuteAsync(req, CancellationToken.None);
    }

    public static Task<SandboxResult> Py(string code, string entry = "f", int timeoutMs = 10_000, params string[] inputs) =>
        Run(SandboxLanguage.Python, code, entry, timeoutMs, SandboxAction.Run, 1, inputs.Length == 0 ? ["[]"] : inputs);

    public static Task<SandboxResult> Cs(string code, string entry = "S.F", int timeoutMs = 10_000, params string[] inputs) =>
        Run(SandboxLanguage.CSharp, code, entry, timeoutMs, SandboxAction.Run, 1, inputs.Length == 0 ? ["[]"] : inputs);

    /// <summary>The single output of a one-case run, as JSON text; fails the test if the case errored.</summary>
    public static string Out(SandboxResult r, int i = 0)
    {
        Assert.True(r.Compiled, r.CompileErrors + r.StdErrTail);
        Assert.Null(r.Cases[i].Error);
        return r.Cases[i].Output!.Value.GetRawText();
    }
}
