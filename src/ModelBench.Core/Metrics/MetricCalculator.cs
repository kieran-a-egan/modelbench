using ModelBench.Core.Config;
using ModelBench.Core.Domain;
using ModelBench.Core.Providers;

namespace ModelBench.Core.Metrics;

/// <summary>Per-attempt metrics with provenance (ADR 0008). Nothing is estimated from text; missing telemetry is Unavailable.</summary>
public static class MetricCalculator
{
    /// <param name="wallTime">Inference time for the whole attempt, including failed tries and back-off.</param>
    /// <param name="usageComplete">False when requests outside <paramref name="responses"/> were made (a retried try, a replaced attempt):
    /// tokens, model calls and cost are then Unavailable rather than understated (MB-009).</param>
    /// <param name="inferenceTime">Time spent inside tries, excluding retry back-off; energy is charged on this (default: wallTime).</param>
    /// <param name="concurrency">Attempts in flight alongside this one (itself included): the machine's watts are shared by them, so each bills 1/N.</param>
    public static List<Metric> Compute(IReadOnlyList<ModelResponse> responses, TimeSpan wallTime, Pricing? pricing, EnergyProfile? energy, bool usageComplete = true,
        TimeSpan? inferenceTime = null, int concurrency = 1)
    {
        var m = new List<Metric>();
        var input = usageComplete ? Sum(responses, u => u.InputTokens) : null;
        var output = usageComplete ? Sum(responses, u => u.OutputTokens) : null;
        var reasoning = usageComplete ? Sum(responses, u => u.ReasoningTokens) : null;
        m.Add(Count(MetricNames.InputTokens, input));
        m.Add(Count(MetricNames.OutputTokens, output));
        m.Add(Count(MetricNames.ReasoningTokens, reasoning));

        var ttft = responses.Count > 0 ? responses[0].Timing.TimeToFirstToken : null;
        m.Add(ttft is { } t ? Make(MetricNames.TimeToFirstTokenMs, t.TotalMilliseconds, "ms", Provenance.Measured) : Unavailable(MetricNames.TimeToFirstTokenMs, "ms"));
        m.Add(Make(MetricNames.DurationMs, wallTime.TotalMilliseconds, "ms", Provenance.Measured));
        m.Add(usageComplete ? Make(MetricNames.ModelCalls, responses.Count, "calls", Provenance.Measured) : Unavailable(MetricNames.ModelCalls, "calls"));

        var firstOut = responses.Count > 0 ? responses[0].Usage.OutputTokens : null;
        var genTime = ttft is { } tt ? responses[0].Timing.Total - tt : (TimeSpan?)null;
        m.Add(firstOut is { } o && genTime is { } g && g > TimeSpan.Zero
            ? Make(MetricNames.ThroughputTps, o / g.TotalSeconds, "tokens/s", Provenance.Calculated)
            : Unavailable(MetricNames.ThroughputTps, "tokens/s"));

        // A server that reports reasoning outside completion tokens would make the split negative: treat cost as unavailable, not invented.
        if (pricing is not null && input is { } i && output is { } outTok && (reasoning ?? 0) <= outTok)
        {
            // Output tokens include reasoning tokens; reasoning is billed at its own rate when one is configured.
            var r = reasoning ?? 0;
            var usd = (i * pricing.Input + (outTok - r) * pricing.Output + r * (pricing.Reasoning ?? pricing.Output)) / 1_000_000m;
            m.Add(Make(MetricNames.CostUsd, (double)usd, "USD", Provenance.Calculated));
        }
        else m.Add(Unavailable(MetricNames.CostUsd, "USD"));

        if (energy is not null)
        {
            var kwh = (inferenceTime ?? wallTime).TotalHours * energy.Watts / 1000.0 / Math.Max(1, concurrency);
            m.Add(Make(MetricNames.EnergyKwh, kwh, "kWh", Provenance.Estimated));
            m.Add(Make(MetricNames.EnergyCostUsd, kwh * (double)energy.PricePerKwh, "USD", Provenance.Estimated));
        }
        else
        {
            m.Add(Unavailable(MetricNames.EnergyKwh, "kWh"));
            m.Add(Unavailable(MetricNames.EnergyCostUsd, "USD"));
        }
        return m;
    }

    /// <summary>Manual import: there was no model call we observed, so nothing is measurable.</summary>
    public static List<Metric> AllUnavailable() =>
    [
        Unavailable(MetricNames.InputTokens, "tokens"), Unavailable(MetricNames.OutputTokens, "tokens"),
        Unavailable(MetricNames.ReasoningTokens, "tokens"), Unavailable(MetricNames.TimeToFirstTokenMs, "ms"),
        Unavailable(MetricNames.DurationMs, "ms"), Unavailable(MetricNames.ModelCalls, "calls"),
        Unavailable(MetricNames.ThroughputTps, "tokens/s"), Unavailable(MetricNames.CostUsd, "USD"),
        Unavailable(MetricNames.EnergyKwh, "kWh"), Unavailable(MetricNames.EnergyCostUsd, "USD"),
    ];

    /// <summary>Sum only if every call reported the value; a partial sum would understate the total.</summary>
    private static long? Sum(IReadOnlyList<ModelResponse> rs, Func<Usage, long?> pick)
        => rs.Count > 0 && rs.All(r => pick(r.Usage) is not null) ? rs.Sum(r => pick(r.Usage)!.Value) : null;

    private static Metric Count(string name, long? v) => v is { } x ? Make(name, x, "tokens", Provenance.Measured) : Unavailable(name, "tokens");

    private static Metric Make(string name, double v, string unit, Provenance p) => new() { Name = name, Value = v, Unit = unit, Provenance = p };

    private static Metric Unavailable(string name, string unit) => new() { Name = name, Value = null, Unit = unit, Provenance = Provenance.Unavailable };
}
