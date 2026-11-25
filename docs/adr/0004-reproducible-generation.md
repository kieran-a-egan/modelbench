# ADR 0004: Reproducible seeded generation

**Status:** Accepted

## Context
Benchmark instances must be reproducible from recorded seeds and generator versions, and must be identical across candidates.

## Decision
- Generators use `Generation.Rng` (xoshiro256** seeded by SplitMix64), never `System.Random`, the clock, GUIDs or hash-set iteration order.
- A task's seed is the first 8 bytes of `SHA256("{master}|{family}|{version}|{index}")`.
- Each family carries an integer `Version`, which is bumped whenever its output for a seed changes.
- Content hashes:
  - task hash = SHA-256 of prompt JSON + `\n` + hidden JSON
  - suite hash = SHA-256 of the task hashes
- Serialization uses the fixed `Json.Options`, so property order follows declaration order.
- Suites are persisted before execution, and runs reference the suite id.
- `suite verify` regenerates the suite and compares every hash. A golden-hash test pins one seed per family.

## Consequences
Changing a generator without bumping its version fails the golden test, by design.
