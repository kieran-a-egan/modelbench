using System.Text;
using System.Text.Json;
using ModelBench.Core.Domain;
using ModelBench.Core.Execution;

namespace ModelBench.Core.Grading;

/// <summary>
/// Config: { required: [{ name, args?: json-subset }], ordered?: bool, forbidden?: [name], maxCalls?: int }.
/// args match when each listed property deep-equals the call's parsed argument (CodeSupport.JsonEqual).
/// Assertions: called[i]:name, order, forbidden:name, max-calls.
/// required/order credit only EXECUTED calls; forbidden/max-calls count every REQUESTED call, executed or not.
/// Executed follows Regrader.Replay: not on the transcript's last message (EpisodeRunner stops at the step limit without
/// executing it) and arguments within MaxToolArgsBytes. Positional, because local servers may repeat or omit call ids.
/// </summary>
public sealed class ToolCallsGrader : IGrader
{
    public string Type => "tool-calls";

    public Task<IReadOnlyList<AssertionOutcome>> GradeAsync(GraderSpec spec, GradingContext ctx)
    {
        var t = ctx.Transcript;
        var requested = t.Where(m => m.Role == Role.Assistant).SelectMany(m => m.ToolCalls ?? []).ToList();
        var calls = t.Take(t.Count - 1).Where(m => m.Role == Role.Assistant).SelectMany(m => m.ToolCalls ?? [])
            .Where(c => Encoding.UTF8.GetByteCount(c.ArgumentsJson) <= EpisodeRunner.MaxToolArgsBytes).ToList();
        var parsed = calls.Select(c => TryParse(c.ArgumentsJson)).ToList();
        var results = new List<AssertionOutcome>();

        var required = spec.Config.TryGetProperty("required", out var r) ? r.EnumerateArray().ToList() : [];
        bool Match(JsonElement req, int k)
            => calls[k].Name == req.GetProperty("name").GetString()
               && (!req.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object || (parsed[k] is { } p && Subset(args, p)));
        for (var i = 0; i < required.Count; i++)
        {
            var name = required[i].GetProperty("name").GetString()!;
            var hits = Enumerable.Range(0, calls.Count).Where(k => Match(required[i], k)).ToList();
            var why = calls.Any(c => c.Name == name) ? $"no '{name}' call had arguments matching {required[i].GetRawText()}" + BadArgs(calls, parsed, name)
                : requested.Any(c => c.Name == name) ? $"'{name}' was requested but never executed" : $"'{name}' was never called";
            results.Add(AssertionOutcome.Check($"called[{i}]:{name}", hits.Count > 0, required[i].GetRawText(), hits.Count > 0 ? calls[hits[0]].ArgumentsJson : null, why));
        }

        if (spec.Config.TryGetProperty("ordered", out var o) && o.ValueKind == JsonValueKind.True)
        {
            // Greedy: each requirement must be satisfied by a call after the previous requirement's call.
            var last = -1;
            var ok = true;
            foreach (var req in required)
            {
                var next = Enumerable.Range(last + 1, Math.Max(0, calls.Count - last - 1)).Where(k => Match(req, k)).Select(k => (int?)k).FirstOrDefault();
                if (next is null) { ok = false; break; }
                last = next.Value;
            }
            results.Add(AssertionOutcome.Check("order", ok, "required calls in listed order", string.Join(",", calls.Select(c => c.Name)), "required calls were missing or out of order"));
        }

        if (spec.Config.TryGetProperty("forbidden", out var f))
            foreach (var n in f.EnumerateArray().Select(x => x.GetString()!))
                results.Add(AssertionOutcome.Check($"forbidden:{n}", requested.All(c => c.Name != n), $"no '{n}' calls", $"{requested.Count(c => c.Name == n)} call(s)", $"forbidden tool '{n}' was called"));

        if (spec.Config.TryGetProperty("maxCalls", out var m) && m.ValueKind == JsonValueKind.Number)
            results.Add(AssertionOutcome.Check("max-calls", requested.Count <= m.GetInt32(), $"<= {m.GetInt32()}", requested.Count.ToString(), $"{requested.Count} tool calls exceeds the limit of {m.GetInt32()}"));
        return Task.FromResult<IReadOnlyList<AssertionOutcome>>(results);
    }

    private static string BadArgs(List<ToolCall> calls, List<JsonElement?> parsed, string name)
    {
        var bad = Enumerable.Range(0, calls.Count).Count(k => calls[k].Name == name && parsed[k] is null);
        return bad > 0 ? $" ({bad} call(s) had unparsable arguments)" : "";
    }

    private static JsonElement? TryParse(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    private static bool Subset(JsonElement expected, JsonElement actual)
        => actual.ValueKind == JsonValueKind.Object && expected.EnumerateObject().All(p => actual.TryGetProperty(p.Name, out var v) && CodeSupport.JsonEqual(p.Value, v));
}
