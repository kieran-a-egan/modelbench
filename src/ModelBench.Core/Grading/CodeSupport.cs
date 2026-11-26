using System.Globalization;
using System.Numerics;
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

    /// <summary>
    /// The one JSON number equality used by every grader. Zero tolerance is exact: both numbers are reduced to a canonical
    /// (sign, integer coefficient without trailing zeros, base-10 exponent) and compared, so 1 == 1.0 == 1e0 and 1e400 != 2e400
    /// without ever materialising a power of ten. A positive tolerance is an absolute difference in double.
    /// </summary>
    public static bool NumberEqual(JsonElement a, JsonElement b, double tol = 0)
    {
        if (tol > 0) return Math.Abs(a.GetDouble() - b.GetDouble()) <= tol;
        return Canonical(a.GetRawText()) == Canonical(b.GetRawText());
    }

    /// <summary>Raw JSON number text (already validated by the parser) as (negative, coefficient, exponent); every zero is (false, 0, 0).</summary>
    private static (bool Negative, BigInteger Coefficient, BigInteger Exponent) Canonical(string raw)
    {
        var negative = raw[0] == '-';
        var e = raw.IndexOfAny(['e', 'E']);
        var mantissa = raw[(negative ? 1 : 0)..(e < 0 ? raw.Length : e)];
        var exponent = e < 0 ? BigInteger.Zero : BigInteger.Parse(raw[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var dot = mantissa.IndexOf('.');
        var digits = dot < 0 ? mantissa : mantissa.Remove(dot, 1);
        if (dot >= 0) exponent -= mantissa.Length - dot - 1;
        var trimmed = digits.TrimEnd('0');
        if (trimmed.Length == 0) return (false, BigInteger.Zero, BigInteger.Zero);
        return (negative, BigInteger.Parse(trimmed, CultureInfo.InvariantCulture), exponent + (digits.Length - trimmed.Length));
    }

    /// <summary>JSON structural equality; numbers compare with NumberEqual, recursively.</summary>
    public static bool JsonEqual(JsonElement a, JsonElement b, double tol = 0)
    {
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Number: return NumberEqual(a, b, tol);
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
        : r.Cases.Where((c, i) => c.Index != i).Any() ? "malformed result: case indices are not 0.." + (expectedCases - 1) + " in order"
        : null;
}
