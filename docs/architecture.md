# ModelBench architecture

ModelBench is a local, deterministic benchmarking application for language models. It is a **modular monolith**:

- **Core** library: all logic.
- **Cli**: the `modelbench` executable.
- **Web**: a Blazor Server dashboard.

Cli and Web share one SQLite database and the same Core services. Candidate-generated code runs only inside Docker sandbox images built from `sandbox/`.

```
              models.json ──► ModelCatalog ──► ResolvedModel ──► ProviderFactory ──► IModelProvider (HTTP/SSE)
                                                                                        ▲
SuiteSpec ──► SuiteGenerator ──► FamilyRegistry/IBenchmarkFamily ──► Suite+TaskInstances (frozen in SQLite)
                                                                                        │
Run (snapshot) ──► RunExecutor ──► Episode (single-turn | tool loop | agentic loop | manual import)
                                     │                     └─► ISimulatedEnvironment (family-owned, in-process)
                                     ▼
                              GradingPipeline ──► AnswerExtractor ──► GraderRegistry/IGrader* ──► ISandbox (Docker)
                                     ▼
                              Attempt + AssertionResults + Metrics (SQLite) ──► Reporting (leaderboards, compare, export)
```

## Projects and namespaces

| Path | Contents |
|---|---|
| `src/ModelBench.Core/Domain` | EF entities (`Suite`, `TaskInstance`, `Run`, `Attempt`, `AssertionResult`, `Metric`), `TaskPrompt`, `TaskHidden`, `GraderSpec`, `ChatMessage`, `Json` helper |
| `src/ModelBench.Core/Generation` | `Rng` (xoshiro256**), `SuiteSpec`, `SuiteGenerator`, content hashing |
| `src/ModelBench.Core/Families` | `IBenchmarkFamily`, `ISimulatedEnvironment`, `FamilyRegistry`, one file (or folder) per family, static/repo loaders |
| `src/ModelBench.Core/Grading` | `IGrader`, `GradingContext`, `AssertionOutcome`, `InfraException`, `AnswerExtractor`, `GradingPipeline`, `GraderRegistry`, one file per grader |
| `src/ModelBench.Core/Providers` | `IModelProvider`, `ProviderFactory`, adapters, `ScriptedProvider` (selftest/tests), SSE reader |
| `src/ModelBench.Core/Config` | `ResolvedModel`, `ModelCatalog` (models.json loader) |
| `src/ModelBench.Core/Execution` | `RunService` (create/resume/cancel), `RunExecutor`, `Episode` runners, `RetryPolicy`, manual export/import, `SelfTest` |
| `src/ModelBench.Core/Sandbox` | `ISandbox`, `DockerSandbox`, sandbox protocol |
| `src/ModelBench.Core/Metrics` | metric computation (cost, energy, throughput, consistency) |
| `src/ModelBench.Core/Persistence` | `BenchDb` (EF Core SQLite), `DbPaths` |
| `src/ModelBench.Core/Reporting` | leaderboard, comparison, failure listing, CSV/JSON export queries |
| `src/ModelBench.Cli` | `Program.cs` + `Commands/*.cs` (System.CommandLine); thin layer over Core |
| `src/ModelBench.Web` | Blazor Server pages over Core; background `RunHostedService` |
| `sandbox/python`, `sandbox/dotnet` | Dockerfiles and in-container runners |
| `benchmarks/static`, `benchmarks/repos` | file-based benchmark definitions |
| `tests/ModelBench.Tests` | xUnit v3 tests; folders mirror Core |

Rules:
- No interface with a single implementation unless it's a contract listed here. The contracts are `IBenchmarkFamily`, `ISimulatedEnvironment`, `IGrader`, `IModelProvider` and `ISandbox`.
- Registries are static lists or dictionaries, not DI containers.
- Use DI only in Web, where ASP.NET requires it.

## Key contracts (source of truth: the code)

- `Families/IBenchmarkFamily.cs`: `Generate(Rng, complexity)` returns a `GeneratedTask`, which has:
  - `Prompt`
  - `Hidden`
  - `ReferenceTurns`
  - `KnownBadTurns`

  `Validate` is an *independent* check. Complexity is always on the 1..5 scale, and it is the "difficulty" axis in reports.
- `Grading/IGrader.cs`: `GradeAsync(GraderSpec, GradingContext)` returns `AssertionOutcome[]`. Graders throw `InfraException` only for infrastructure failure.
- `Providers/IModelProvider.cs`: `CompleteAsync(ModelRequest)` returns a `ModelResponse` carrying usage (null = unavailable), timing, and the raw request and response.
- `Sandbox/ISandbox.cs`: `SandboxRequest` carries code plus inputs only. It structurally cannot carry expected outputs.
- `Config/ResolvedModel.cs`: the resolved model; `ApiKey` is never persisted.

## Data flow invariants

1. **Prompt/Hidden split.**
   - `TaskInstance.PromptJson` is a `TaskPrompt`, the only source for building provider requests (`EpisodeRunner` builds messages from `TaskPrompt` alone).
   - `TaskInstance.HiddenJson` is a `TaskHidden`, read only by `GradingPipeline`, the families' `CreateEnvironment`, and selftest.
2. **Suites are frozen.** `SuiteGenerator` persists all instances before any run can reference the suite. There is no update path for `TaskInstance`.
3. **Runs snapshot their inputs.**
   - `Run.ModelSnapshotJson` is `ResolvedModel` serialized without `ApiKey` and with header values replaced by `"***"`.
   - `Run.EnvironmentJson` holds OS, .NET runtime, Docker server version, sandbox image IDs and the app git SHA.
4. **Identical instances across candidates.** Comparisons are only made between runs on the same `SuiteId`, or between suites whose `ContentHash` is equal.

## Benchmark generation

- `SuiteSpec { Name, MasterSeed, Families: [{ Family, Complexity, Count }] }`. Generator versions are recorded per entry at generation time.
- Each task's seed is `Rng.DeriveSeed(masterSeed, familyId, familyVersion, index)`. The family gets `new Rng(seed)`.
- `TaskInstance.ContentHash` is `SHA256(PromptJson + "\n" + HiddenJson)` as lowercase hex. `Suite.ContentHash` is `SHA256` of the task hashes joined by `\n`, in index order.
- `Validate` runs on every generated task. Generation fails loudly if validation fails, so it never retries silently with another seed. A family that needs rejection sampling does it *inside* `Generate`, using the same `Rng`.
- `suite verify <id>` regenerates the suite from `SpecJson` and compares every hash.
- Static definitions (`benchmarks/static/*.json`) and repository definitions (`benchmarks/repos/<name>/benchmark.json`) are exposed as families with ids `static:<name>` and `repo:<name>`. Their Version is a content hash of the definition files. `Generate` ignores rng and complexity comes from the file.

## Execution

Modes are `SingleTurn`, `ToolAssisted`, `Agentic` and `Manual`. A run has exactly one mode, and leaderboards are separated by mode. A run executes only the suite's tasks whose `TaskInstance.Mode` matches the run mode; `Manual` runs execute `SingleTurn` tasks.

Episodes:
- **Single-turn**: one model call, `[system?, user]`.
- **Tool-assisted**:
  - The model gets `TaskPrompt.Tools`, and tool calls run against `family.CreateEnvironment(hidden)`.
  - The loop continues until the model returns text with no tool calls, or the step limit (8) is reached.
  - Graded on the final answer plus the tool calls in the transcript.
- **Agentic**: the same loop with a step limit of 30, graded mainly on final environment state.
- **Manual**:
  - `manual export` writes `prompts.jsonl` (`{runId, taskId, repeat, system, user, answerFormat}`) and `prompts.md`.
  - `manual import` reads `responses.jsonl` (`{taskId, repeat, response}`) and grades each response as a single-turn episode.
  - All telemetry is Unavailable.

`RunExecutor`:
- Work items are (task × repeat) pairs, minus attempts already in a terminal outcome. This is what makes resume work.
- Workers read from a `Channel`, with `Run.Concurrency` workers.
- Each attempt has a timeout of `Run.TimeoutSeconds`, enforced with a linked `CancellationTokenSource`. Exceeding it records outcome `Timeout`.
- Retry follows `RetryPolicy { MaxRetries, BaseDelayMs, MaxDelayMs }` with exponential backoff, and applies **only** to `InfraException.Retryable`.
- When retries are exhausted, or the exception is not retryable, the outcome is `InfraError`.
- Cancellation (Ctrl+C or the web cancel button) stops queuing, marks in-flight attempts `Cancelled` and sets the run to `Cancelled`. `run resume` continues the run later.
- Each attempt is persisted as soon as it finishes.

## Grading

`GradingPipeline.GradeAsync(task, transcript, finalEnvState, sandbox)` does the following:
1. Strips `<think>…</think>` / `<thinking>…</thinking>` from the final text.
2. Extracts the answer with `AnswerExtractor` according to `TaskPrompt.AnswerFormat`:
   - `Tagged`: the last `<answer>…</answer>`.
   - `CodeBlock`: the last fenced block.
   - `Json`: tagged or fenced content that must parse.
   - `FreeText`: the trimmed text.
   - If extraction fails, it emits the required assertion `answer-format` with a score of 0.
3. Runs every `GraderSpec` through `GraderRegistry`. Each outcome becomes an `AssertionResult` that inherits the spec's weight and required flag.
4. Computes **Score** as Σ(weight·score) / Σ(weight), and **Passed** as all required assertions passing.

A grader that cannot run because the answer is missing still emits its assertions as failed, with diagnostic `"no answer"`. Every grader runs, so failure reports are complete.

Grader type ids (`GraderSpec.Type`) and their `Config`:

| Type | Config | Assertions |
|---|---|---|
| `exact` | `{ expected: string, normalize?: "none"\|"trim"\|"whitespace"\|"casefold" }` | one |
| `structural` | `{ expected: json, unorderedArrays?: bool }` | one per mismatching JSON path (or a single pass) |
| `numeric` | `{ expected: "p/q" or number string, absTol?: number, relTol?: number }` | one |
| `json-schema` | `{ schema: json }` | one per schema error (or a single pass) |
| `text-constraints` | `{ constraints: [{ kind, ... }] }`, where kind is one of: word-count, max-words, contains, not-contains, regex, line-count, starts-with, ends-with, all-lowercase, no-letter, json-keys, bullet-count | one per constraint |
| `constraint` | `{ problem: "knapsack"\|"schedule"\|"coloring"\|..., instance: json, optimum?: number }` | one per constraint, plus `optimal` |
| `compile` | `{ language, entryPoint }` | one |
| `unit-tests` | `{ language, entryPoint, cases: [{ args: [..], expected: json }], tolerance?: number }` | one per case |
| `differential` | same shape as unit-tests; cases are seeded random inputs whose expected values come from the host reference solver | one per case |
| `performance` | `{ language, entryPoint, args: [..], expected: json, budgetMs: number }`. Runs the case alone with the sandbox `TimeoutMs = budgetMs`. Within-budget means not killed by the **host** wall clock; in-container timings are informational only, because candidate code can forge them | `correct`, `within-budget` |
| `ast` | `{ language, rules: [{ kind: "defines"\|"forbids-import"\|"forbids-call"\|"requires-loop"\|"forbids-recursion", name? }] }` | one per rule |
| `tool-calls` | `{ required: [{ name, args?: json-subset, }], ordered?: bool, forbidden?: [name], maxCalls?: int }` | one per requirement |
| `env-state` | `{ expected: json-subset }` (dot-paths into the final state) | one per path |
| `canary` | `{ canaries: [string], forbiddenTools?: [name] }`, scanning the final text and all tool-call arguments | one per canary/tool |

The `unit-tests`, `differential` and `performance` graders send `SandboxRequest(code, entryPoint, inputs)`. They compare the returned outputs with the expected values **on the host**.

## Sandbox (ADR 0003)

`DockerSandbox` drives the `docker` CLI and talks to the in-container runner over a simple protocol:
- It pipes the request JSON (code, entryPoint, inputs, action, repetitions, nonce) to the container's **stdin** (`docker run -i`). Nothing is mounted. The runner reads stdin fully before loading candidate code.
- The runner prints one line, `MBRESULT:<nonce>:<base64 json>`. The nonce is random per execution.
- Images are `modelbench-python:1` and `modelbench-dotnet:1`.
- Container flags: `--rm --network none --read-only --tmpfs /tmp:rw,size=64m --memory 256m --memory-swap 256m --cpus 1 --pids-limit 64 --cap-drop ALL --security-opt no-new-privileges --user 65534:65534 -e HOME=/tmp -i`.
- The host enforces a wall-clock limit of `TimeoutMs` + 5 s grace by running `docker kill`, and caps captured output at 1 MB.

## Metrics (ADR 0008)

Per attempt, summed over the episode's model calls:
- `input_tokens`, `output_tokens`, `reasoning_tokens`: Measured if the provider reported them, otherwise Unavailable.
- `ttft_ms`: Measured from the first model call.
- `duration_ms`: Measured, wall time of the whole episode.
- `throughput_tps`: Calculated as output tokens / (total − ttft) for the first call, and only when both are measured.
- `model_calls`: Measured.
- `cost_usd`: Calculated from tokens × `Pricing` (reasoning tokens billed at the `Reasoning` rate if set, else at the output rate). Unavailable when tokens or pricing are missing.
- `energy_kwh` and `energy_cost_usd`: Estimated from duration × `Energy.Watts` and the price.

Run level:
- pass rate and mean score, excluding InfraError/Timeout/Cancelled
- **consistency**: the fraction of tasks with the same pass/fail outcome on every repeat
- cost per successful task: Σcost / passes

## Configuration

- **Models**: `models.json`, a superset of pi's `~/.pi/agent/models.json`. The CLI looks for `--models <path>`, then `./models.json`, then `~/.modelbench/models.json`. See `models.example.json` and the user guide.
- **Database**: the `--db <path>` option, else the `MODELBENCH_DB` env var, else `~/.modelbench/modelbench.db`.
- **Secrets**: `apiKey: "{env:NAME}"` is resolved at request time.
