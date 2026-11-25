using System.Text.Json;
using ModelBench.Core.Sandbox;

namespace ModelBench.Core.Grading;

/// <summary>Shared by the sandbox-backed code graders.</summary>
internal static class CodeSupport
{
    /// <summary>Per-call limit for unit-tests/differential. The host kills the container at TimeoutMs x calls + grace.</summary>
    public const int CaseTimeoutMs = 2_000;
    public const int CompileTimeoutMs = 10_000;

    public static readonly JsonElement Null = JsonDocument.Parse("null").RootElement.Clone();

    public static SandboxLanguage Language(JsonElement config) => config.GetProperty("language").GetString() switch
    {
        "python" => SandboxLanguage.Python,
        "csharp" => SandboxLanguage.CSharp,
        var l => throw new ArgumentException($"unknown language '{l}'"),
    };

    /// <summary>JSON structural equality; numbers match within an absolute tolerance, recursively.</summary>
    public static bool JsonEqual(JsonElement a, JsonElement b, double tol = 0)
    {
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Number: return Math.Abs(a.GetDouble() - b.GetDouble()) <= tol;
            case JsonValueKind.String: return a.GetString() == b.GetString();
            case JsonValueKind.Array:
                return a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray()).All(p => JsonEqual(p.First, p.Second, tol));
            case JsonValueKind.Object:
                var props = a.EnumerateObject().ToList();
                return props.Count == b.EnumerateObject().Count() && props.All(p => b.TryGetProperty(p.Name, out var v) && JsonEqual(p.Value, v, tol));
            default: return true; // true/true, false/false, null/null
        }
    }

    public static string Actual(Sandbox.CaseResult c) => c.Error ?? c.Output?.GetRawText() ?? "null";

    /// <summary>Why a result carries no usable cases, or null when it does.</summary>
    public static string? Unusable(SandboxResult r, int expectedCases) =>
        r.TimedOut ? "timed out"
        : r.CompileErrors is not null ? "did not compile: " + r.CompileErrors
        : r.Cases.Count != expectedCases ? "crashed: " + r.StdErrTail
        : null;
}
