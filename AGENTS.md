# ModelBench: agent notes

Read `docs/architecture.md` (contracts, grader configs, invariants) and `docs/acceptance-criteria.md` before changing code.

## Commands
- Build: `dotnet build -warnaserror`. Warnings are errors via Directory.Build.props.
- Test: `dotnet test` (global.json selects the Microsoft.Testing.Platform runner). To run a subset, run the test exe directly after a build: `tests/ModelBench.Tests/bin/Debug/net10.0/ModelBench.Tests.exe -class "ModelBench.Tests.Namespace.ClassName"`; `-method`, `-namespace` and wildcards (`-class "*Regrade*"`) also work.
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
