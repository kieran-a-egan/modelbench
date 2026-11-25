# ModelBench: agent notes

Read `docs/architecture.md` (contracts, grader configs, invariants) and `docs/acceptance-criteria.md` before changing code.

## Commands
- Build: `dotnet build -warnaserror`. Warnings are errors via Directory.Build.props.
- Test: `dotnet test`. To run a subset, pass `--filter-class` or `--filter-method` after `--`. This is xUnit v3, so use `dotnet test -- --filter-class "*Name*"`.
- CLI: `dotnet run --project src/ModelBench.Cli -- <args>`
- Web: `dotnet run --project src/ModelBench.Web`
- Sandbox images: `dotnet run --project src/ModelBench.Cli -- sandbox build`

## Rules
- Never use an LLM for grading. Graders are pure functions of `GradingContext`, plus the sandbox.
- Generators: use `Rng` only. No `System.Random`, clock, `Guid.NewGuid`, or `HashSet`/`Dictionary` iteration order in output.
- Provider requests are built from `TaskPrompt` only. Hidden data never goes to a provider or into a `SandboxRequest`.
- Never run candidate code on the host. Use `ISandbox`.
- Missing telemetry is recorded as `Provenance.Unavailable` and never estimated.
- No placeholder, stub or TODO implementations. If something can't be done, report it instead.
- Keep code minimal: no single-implementation interfaces beyond the contracts in architecture.md, and no speculative options.
- Every grader, family and adapter needs tests with both passing and failing cases.
