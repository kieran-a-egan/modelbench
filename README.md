# ModelBench

ModelBench is a local, deterministic benchmarking app for language models, written in C# on .NET 10. It generates frozen, seeded benchmark suites, runs them against local models served by any OpenAI-compatible server (Strata, Ollama, LM Studio, llama.cpp, vLLM, ...), grades every answer with plain code, and stores everything in SQLite so runs can be inspected, compared and re-graded. It has a CLI (`modelbench`) and a Blazor Server dashboard that share one database.

## Non-negotiables

- **Deterministic grading, no LLM judge.** Graders are pure functions of the task's hidden data and the model's transcript. Code answers are executed in a Docker sandbox, and the results are compared on the host. `run regrade --check` proves that stored scores reproduce.
- **Suites are frozen before any run.** A suite is generated from a seed, persisted with content hashes, and re-verifiable (`suite verify`).
- **Hidden data never reaches a model or the sandbox.** Prompts are built from the visible `TaskPrompt` only.
- **No fabricated telemetry.** A token count, time or cost that the provider did not report is stored as `Unavailable`. Derived values are labelled `Calculated` or `Estimated`.
- **Candidate code never runs on the host.** It runs in a locked-down container (no network, read-only root, non-root, memory/pid/CPU limits).

See `docs/architecture.md`, `docs/threat-model.md` and `docs/acceptance-criteria.md` for the details, and `docs/user-guide.md` for how to use it.

## Prerequisites

- .NET 10 SDK
- Docker, for the code families (code-generation, debugging) and `selftest`. Everything else works without it.

## Install

```
./install.ps1
```

This publishes the CLI and the dashboard into `~/.modelbench/bin` (with `benchmarks/` and `sandbox/`) and adds that folder to your user PATH. Open a new terminal afterwards. Re-run it after pulling to update. It works from Windows PowerShell or `pwsh` on macOS/Linux; on macOS/Linux it prints the `PATH` line to add to your shell profile.

## Quickstart

```
# 1. Build the sandbox images (once)
modelbench sandbox build

# 2. Describe your models: copy models.example.json to ~/.modelbench/models.json and edit it,
#    or point MODELBENCH_MODELS at a pi models file (set it once, e.g. setx MODELBENCH_MODELS %USERPROFILE%\.pi\agent\models.json)
cp models.example.json ~/.modelbench/models.json

# 3. Generate a suite: 3 tasks of mathematics at every complexity, and 3 debugging tasks at complexity 2
modelbench suite generate --name first --seed 1 --family mathematics:all:3 --family debugging:2:3

# 4. Run it
modelbench run start --suite <suite-id> --model strata/inkling-2-mini-sq-15

# 5. Inspect scores and every failed assertion
modelbench run show <run-id> --failures

# 6. Compare models and runs
modelbench leaderboard
modelbench compare <run-a> <run-b>

# 7. Open the dashboard (same database and models file; Ctrl+C stops it)
modelbench web
```

Ids may be abbreviated to any unique prefix (the CLI prints 8 characters). The database defaults to `~/.modelbench/modelbench.db` (`--db` or `MODELBENCH_DB` override it). `models.json` is looked up as `--models`, then `MODELBENCH_MODELS`, then `./models.json`, then `~/.modelbench/models.json`. `modelbench web` passes the same database and models file to the dashboard; `--port` changes the port (default 5180) and `--no-browser` skips opening a browser.

From a checkout without installing, use `dotnet run --project src/ModelBench.Cli -- <args>` and `dotnet run --project src/ModelBench.Web`.

Check the installation with `modelbench selftest`, which replays every family's reference and known-bad answers through the real grading pipeline.

## Benchmark families

The families below are the ones registered in `FamilyRegistry`. Complexity is always 1 to 5 and is the difficulty axis in reports.

| Id | Category | Mode | What complexity scales |
|---|---|---|---|
| `mathematics` | mathematics | SingleTurn | Operand sizes and expression length (exact rational, modular and combinatorial answers) |
| `instruction-fidelity` | instruction-fidelity | SingleTurn | Number of mechanically checked constraints (2 x complexity) |
| `data-transformation` | data-transformation | SingleTurn | Filters, group keys, aggregates and the limit applied to a CSV/JSON table |
| `policy-evaluation` | policy-evaluation | SingleTurn | Conditions per rule and the precedence mode of a prose access policy |
| `long-context-retrieval` | long-context-retrieval | SingleTurn | Haystack length and the number of linked records to follow (1 to 3 hops) |
| `code-generation` | code-generation | SingleTurn | Task pool, number of random differential cases (5 to 29); performance budget from 3, AST rules from 4 (needs Docker) |
| `debugging` | debugging | SingleTurn | Number of seeded bugs in a Python function (1 to 3) (needs Docker) |
| `constraint-satisfaction` | constraint-satisfaction | SingleTurn | Instance size: knapsack 5 to 25 items, scheduling 4 to 8 tasks, colouring 5 to 14 vertices |
| `deductive-reasoning` | deductive-reasoning | SingleTurn | Logic-grid size, 3x3 up to 5x5 |
| `repository-comprehension` | repository-comprehension | SingleTurn | Size of the synthetic repository and distractor content |
| `tool-calling` | tool-calling | ToolAssisted | Length of the tool-call chain (1 to 6 calls across record, exchange-rate, stock and calculator tools) |
| `adversarial-robustness` | adversarial-robustness | SingleTurn | Number of prompt injections in a document (1 to 6); echoing the canary or decoy fails |
| `adversarial-tools` | adversarial-tools | ToolAssisted | Documents read (1 to 4) and injection styles in tool results; calling the dangerous tools or echoing the canary fails |
| `agentic-task-completion` | agentic-task-completion | Agentic | Required state changes in a simulated file tree and ticket tracker (1 to 10) and look-alike distractors; graded on final state |
| `static:instruction-following` | instruction-following | SingleTurn | Fixed by the file (`benchmarks/static`) |
| `static:unit-conversion` | mathematics | SingleTurn | Fixed by the file (`benchmarks/static`) |
| `repo:inventory-service` | repository-comprehension | SingleTurn | Fixed by the file (`benchmarks/repos`) |

Add your own with a JSON file in `benchmarks/static` or `benchmarks/repos`; see the user guide.

## Layout

| Path | Contents |
|---|---|
| `src/ModelBench.Core` | All logic: generation, families, grading, providers, execution, persistence, reporting |
| `src/ModelBench.Cli` | The `modelbench` executable |
| `src/ModelBench.Web` | The Blazor Server dashboard |
| `sandbox/` | Dockerfiles and runners for the code sandbox |
| `benchmarks/` | File-defined benchmarks |
| `tests/ModelBench.Tests` | xUnit v3 tests; `dotnet test` runs them |
| `docs/` | Architecture, ADRs, threat model, acceptance criteria, user guide |
