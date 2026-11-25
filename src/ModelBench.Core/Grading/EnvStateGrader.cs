using System.Text.Json;
using ModelBench.Core.Domain;

namespace ModelBench.Core.Grading;

/// <summary>
/// Config: { expected: json-subset }, compared with GradingContext.FinalEnvironmentState.
/// <c>expected</c> is flattened to leaf paths (object keys joined by '.', array items as [i]); each leaf is one assertion
/// that deep-equals the value at that position of the actual state. Navigation walks the JSON structure, so keys that
/// themselves contain '.' or '/' (file paths) are fine. An empty object/array is a leaf compared by deep equality.
/// Explicit absence: <c>{"path": null}</c> passes only when the key is missing or null in the actual state.
/// A null final state fails every assertion with "no environment state".
/// </summary>
public sealed class EnvStateGrader : IGrader
{
    public string Type => "env-state";

    public Task<IReadOnlyList<AssertionOutcome>> GradeAsync(GraderSpec spec, GradingContext ctx)
        => Task.FromResult(Evaluate(spec.Config.GetProperty("expected"), ctx.FinalEnvironmentState));

    public static IReadOnlyList<AssertionOutcome> Evaluate(JsonElement expected, JsonElement? state)
    {
        var results = new List<AssertionOutcome>();
        Walk(expected, state, state is null, "", results);
        return results;
    }

    private static void Walk(JsonElement exp, JsonElement? act, bool noState, string path, List<AssertionOutcome> results)
    {
        switch (exp.ValueKind)
        {
            case JsonValueKind.Object when exp.EnumerateObject().Any():
                foreach (var p in exp.EnumerateObject())
                    Walk(p.Value, act is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(p.Name, out var v) ? v : null, noState, path.Length == 0 ? p.Name : $"{path}.{p.Name}", results);
                return;
            case JsonValueKind.Array when exp.GetArrayLength() > 0:
                var i = 0;
                foreach (var item in exp.EnumerateArray())
                {
                    Walk(item, act is { ValueKind: JsonValueKind.Array } a && i < a.GetArrayLength() ? a[i] : null, noState, $"{path}[{i}]", results);
                    i++;
                }
                return;
        }
        var absent = act is null or { ValueKind: JsonValueKind.Null };
        var ok = !noState && (exp.ValueKind == JsonValueKind.Null ? absent : !absent && Equal(exp, act!.Value));
        var expectedText = exp.ValueKind == JsonValueKind.Null ? "absent" : exp.GetRawText();
        var actualText = absent ? "missing" : act!.Value.GetRawText();
        results.Add(AssertionOutcome.Check(path, ok, expectedText, actualText,
            noState ? "no environment state" : exp.ValueKind == JsonValueKind.Null ? $"{path} should be absent but is {actualText}" : $"{path}: expected {expectedText} but got {actualText}"));
    }

    private static bool Equal(JsonElement a, JsonElement b) => a.ValueKind == b.ValueKind && a.ValueKind switch
    {
        JsonValueKind.String => a.GetString() == b.GetString(),
        JsonValueKind.Number => a.GetDouble() == b.GetDouble(),
        JsonValueKind.Array => a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray()).All(t => Equal(t.First, t.Second)),
        JsonValueKind.Object => a.EnumerateObject().Count() == b.EnumerateObject().Count() && a.EnumerateObject().All(p => b.TryGetProperty(p.Name, out var v) && Equal(p.Value, v)),
        _ => true, // true/true, false/false
    };
}
