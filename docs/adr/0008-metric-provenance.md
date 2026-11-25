# ADR 0008: Metric provenance; no fabricated telemetry

**Status:** Accepted

## Decision
Every `Metric` row has a `Provenance`:

| Provenance | Used for |
|---|---|
| **Measured** | provider-reported usage, wall-clock timings, call counts |
| **Calculated** | deterministic arithmetic over measured values and configured prices (cost, throughput) |
| **Estimated** | modelled quantities (local energy = duration × configured watts) |
| **Unavailable** | the value could not be obtained. Recorded explicitly with a null value, never filled in |

- Token counts are never estimated from text length.
- Aggregates skip Unavailable values and show the coverage ("n/m attempts").

## Consequences
Cost comparisons can be partial. The UI shows coverage so partial numbers are not mistaken for totals.
