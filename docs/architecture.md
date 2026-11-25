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
| `src/ModelBench.Core/Execution` | `RunService` (create/resume/cancel), `RunExecutor`, `Episode` runners, `RetryPolicy`, manual export/import, `SelfTest`, `Regrader` (read-only re-grading of stored attempts) |
| `src/ModelBench.Core/Sandbox` | `ISandbox`, `DockerSandbox`, sandbox protocol |
| `src/ModelBench.Core/Metrics` | metric computation (cost, energy, throughput, consistency) |
| `src/ModelBench.Core/Persistence` | `BenchDb` (EF Core SQLite), `DbPaths` |
| `src/ModelBench.Core/Reporting` | leaderboard, comparison, failure listing, CSV/JSON export queries |
| `src/ModelBench.Cli` | `Program.cs` + `Commands/*.cs` (System.CommandLine); thin layer over Core |
| `src/ModelBench.Web` | Blazor Server pages over Core; `RunHostService` (`RunHostService.cs`), a background service that queues and executes runs, including resumes |
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
- Procedural prompts are distinct within a suite. If a prompt repeats, the seed is re-derived with familyId `"<id>#<attempt>"` (attempt 1..49), so the result is still deterministic and `verify` reproduces it. File-defined families repeat tasks by design.
- `TaskInstance.ContentHash` is `SHA256(PromptJson + "\n" + HiddenJson)` as lowercase hex. `Suite.ContentHash` is `SHA256` of the task hashes joined by `\n`, in index order.
- `Validate` runs on every generated task. Generation fails loudly if validation fails, so it never retries silently with another seed. A family that needs rejection sampling does it *inside* `Generate`, using the same `Rng`.
- `suite verify <id>` regenerates the suite from `SpecJson` and compares every hash.
- Static definitions (`benchmarks/static/*.json`) and repository definitions (`benchmarks/repos/<name>/benchmark.json`) are exposed as families with ids `static:<name>` and `repo:<name>`. Their `Version` is the integer `version` from the definition file. The content hash of the definition (the JSON plus any prompt files or included repository files) is stored in every task's `TaskHidden.Reference` (`{contentHash, task}`), so editing a definition changes the task hash even when `version` is not bumped. `Generate` ignores rng and complexity comes from the file; task *i* of an entry is definition task *i* modulo the task count.

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
- Work items are (task x repeat) pairs, minus attempts already in a terminal outcome. They are queued in (task index, repeat) order; completion order is not deterministic when `Run.Concurrency` > 1. This is what makes resume work.
- `Run.Concurrency` workers read from a `Channel`. Each worker has its own `BenchDb` context (DbContext is not thread-safe); each attempt is persisted as soon as it finishes, and the unique (RunId, TaskId, RepeatIndex) index guards against duplicates. `AttemptCompleted` is raised serially under a lock.
- Each attempt has a timeout of `Run.TimeoutSeconds`, enforced with a linked `CancellationTokenSource`. Exceeding it records outcome `Timeout`. There is no overall run deadline.
- Retry follows `RetryPolicy { MaxRetries, BaseDelayMs, MaxDelayMs }` (`run start --max-retries`) and applies **only** to `InfraException.Retryable`. The delay is `base * 2^n` with equal jitter (50-100%), derived from a hash of (run, task, repeat, retry) so it is deterministic. A `Retry-After` on 429/503 (carried by `RetryAfterException`, thrown by `ProviderHttp`) is honoured when longer than the backoff. Every delay is capped at `MaxDelayMs`. `Attempt.Retries` counts retries.
- When retries are exhausted, or the exception is not retryable, the outcome is `InfraError`. Sandbox `InfraException`s during grading are `InfraError` too. An empty or wrong-format answer is `Failed` and is never retried. `InfraError`, `Timeout` and `Cancelled` are excluded from scores.
- Cancellation:
  - Ctrl+C, the web cancel button, or `run cancel <id>` stops queuing and marks in-flight attempts `Cancelled`. The run becomes `Cancelled`.
  - `run cancel` (and the web button, besides its in-process token) sets `Run.CancelRequested`. The executor polls it every 2 s and cancels its own token, so it works from another process. The flag is cleared when an executor starts.
- The dashboard offers Resume for Cancelled, Failed and Running runs (`RunHostService.Resume`: `PrepareResume`, then `ExecuteAsync(retryInfra)`); a refusal message is shown on the page. It has no `--allow-config-change` equivalent.
- Resume (`run resume <id> [--retry-infra] [--allow-config-change]`):
  - It re-executes missing, `Pending` and `Cancelled` attempts. A run left `Running` by a crashed process resumes the same way. The CLI cannot tell a crashed run from a live one in another process, so do not resume a run that is still executing.
  - With `--retry-infra`, `InfraError` and `Timeout` rows are deleted and redone. The old row's `Retries` is added to the new attempt's `Retries` (the cumulative count of retries across executions).
  - `RunService.PrepareResume` first checks that the suite exists and that its task hashes and `Suite.ContentHash` still match. It then re-resolves the model by `Run.ModelRef` and compares its snapshot with `Run.ModelSnapshotJson`. A difference is refused with a per-field diff, unless `--allow-config-change` is given; the run label then gets the suffix `[config changed on resume]`. The stored snapshot is kept unchanged.
- The environment fingerprint (`Run.EnvironmentJson`) holds OS, .NET runtime, processor count, app version, a hash of the host name, git SHA and sandbox image ids.

## Persistence

`BenchDb.Open` applies EF Core migrations, enables WAL and a 30 s busy timeout, and keeps the connection open for the life of the context. Migrations (`src/ModelBench.Core/Persistence/Migrations`; add one with `dotnet tool run dotnet-ef migrations add <Name> --project src/ModelBench.Core --output-dir Persistence/Migrations`). A database created before migrations existed (tables, no `__EFMigrationsHistory`) is rejected with a message to delete or rename it. There is no converter.

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
| `structural` | `{ expected: json, unorderedArrays?: bool, key?: string }` (key = compare only that top-level property of the answer object) | one per mismatching JSON path (or a single pass) |
| `numeric` | `{ expected: "p/q" or number string, absTol?: number, relTol?: number }` | one |
| `json-schema` | `{ schema: json }` | `parses`, then one per schema error (named by instance location), or a single `schema` pass |
| `text-constraints` | `{ constraints: [{ kind, ... }] }`, where kind is one of: word-count `{min?,max?}`, max-words `{max}`, contains / not-contains `{text,caseSensitive?}`, regex `{pattern}`, line-count `{min?,max?}`, starts-with / ends-with `{text}`, all-lowercase, no-letter `{letter}`, json-keys `{keys,exact?}`, bullet-count `{count}` | one per constraint, named `<kind>[i]` |
| `constraint` | `{ problem: "knapsack"\|"schedule"\|"coloring", instance: json, optimum?: number, optimalOnly?: bool }` (`optimalOnly` emits only `optimal`, so a family can weight feasibility and optimality separately) | `parses`, one per constraint, plus `optimal` when `optimum` is set |
| `compile` | `{ language: "python"\|"csharp", entryPoint }` | `compiles` |
| `unit-tests` | `{ language, entryPoint, cases: [{ args: [..], expected: json }], tolerance?: number }` | one per case, `case[i]` |
| `differential` | same shape as unit-tests; cases are seeded random inputs whose expected values come from the host reference solver | one per case, `random[i]` |
| `performance` | `{ language, entryPoint, args: [..], expected: json, budgetMs: number }`. Runs the case alone with the sandbox `TimeoutMs = budgetMs`. Within-budget means not killed by the **host** wall clock; in-container timings are informational only, because candidate code can forge them | `correct`, `within-budget` |
| `ast` | `{ language, rules: [{ kind: "defines"\|"forbids-import"\|"forbids-call"\|"requires-loop"\|"forbids-recursion", name? }] }` | one per rule |
| `tool-calls` | `{ required: [{ name, args?: json-subset }], ordered?: bool, forbidden?: [name], maxCalls?: int }` | `called[i]:name` per requirement, `order`, `forbidden:name`, `max-calls` |
| `env-state` | `{ expected: json-subset }`, flattened to leaf paths (object keys joined by `.`, array items as `[i]`) and compared by deep equality with the final environment state; `{"path": null}` asserts absence; a null state fails every assertion | one per leaf path |
| `canary` | `{ canaries: [string], forbiddenTools?: [name] }`. Scans the final text, every assistant message and every tool-call argument (never tool results or user/system messages). Canary and haystack are normalised to lower-case letters and digits only; homoglyphs are not detected | `no-canary:<first 8 chars>…` per canary, `not-called:<tool>` per forbidden tool |

The `unit-tests`, `differential` and `performance` graders send `SandboxRequest(code, entryPoint, inputs)`. They compare the returned outputs with the expected values **on the host**.

`modelbench run regrade <id> [--check]` (`Execution/Regrader.cs`) re-runs the pipeline on every stored Passed/Failed attempt from its stored transcript plus the task's PromptJson/HiddenJson. For tool and agentic attempts the final environment state is rebuilt by replaying the transcript's tool calls into a fresh `family.CreateEnvironment(hidden)`. It compares assertions (grader, name, expected, actual, score, passed), the attempt score and the outcome, reports differences, and never writes. With `--check` it exits non-zero on any difference (AC-01). Code families need the sandbox.

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
