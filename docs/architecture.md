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
- Each procedural family has a finite visible-prompt capacity per complexity, and a suite never repeats a prompt silently (MB-006). A family whose capacity is a small fixed number declares it (`IBenchmarkFamily.Capacity`), and `SuiteGenerator` refuses a larger count up front with that number. For other families, prompt collisions are retried with up to 1000 deterministic seed salts before failing with `ran out after N (the family's capacity at this complexity)`, or because an earlier entry of the same family used them. Dedup is suite-wide while the up-front check is per entry, so complexities that share prompts share capacity (see the table).
- `code-generation` (v4, #4) draws from 22 task variants. Each variant is its own visible specification (for example kth smallest, kth largest or kth smallest distinct; intervals that touch merged or kept apart), with its own reference solver, an algorithmically different checker for `Validate`, and a reference answer, a wrong answer and, for the performance tasks, a quadratic answer. Examples are fixed per variant and only the hidden inputs are random, so each variant gives one prompt per complexity:

  | Complexity | Pool | Capacity |
  |---|---|---|
  | 1 | tasks without a performance case (10 C#, 2 Python) | 12 |
  | 2 | every variant | 22 |
  | 3–5 | Python tasks with a performance case | 10 |

  Capacity is per suite, not per entry: complexity 1 is a subset of 2, and 3, 4 and 5 draw from the same 10 prompts (4 and 5 are identical), so for example 12 at complexity 1 plus 22 at complexity 2 passes the per-entry check and then fails during generation.
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
- `Run.Concurrency` workers read from a `Channel`. Each worker has its own `BenchDb` context (DbContext is not thread-safe); each attempt is persisted as soon as it finishes, in one transaction that first renews the run lease with a conditional UPDATE on the owner token (a stale owner whose lease expired or was taken over commits nothing; redo deletion and the final status write are fenced the same way, and a heartbeat fault stops the work), and the unique (RunId, TaskId, RepeatIndex) index guards against duplicates. `AttemptCompleted` is raised serially under a lock.
- Each attempt has a timeout of `Run.TimeoutSeconds`, enforced with a linked `CancellationTokenSource`. Exceeding it records outcome `Timeout`. The deadline covers inference (including retries) only: grading runs outside it, bounded by the sandbox's own limits, so a slow optional performance run can never turn a correct answer into a `Timeout`. There is no overall run deadline.
- Retry follows `RetryPolicy { MaxRetries, BaseDelayMs, MaxDelayMs }` (`run start --max-retries`) and applies **only** to `InfraException.Retryable`. The delay is `base * 2^n` with equal jitter (50-100%), derived from a hash of (run, task, repeat, retry) so it is deterministic. A `Retry-After` on 429/503 (carried by `RetryAfterException`, thrown by `ProviderHttp`) is honoured when longer than the backoff, up to 10 minutes; the backoff alone is capped at `MaxDelayMs`. `Attempt.Retries` counts retries.
- When retries are exhausted, or the exception is not retryable, the outcome is `InfraError`. Sandbox `InfraException`s during grading are `InfraError` too. An empty or wrong-format answer is `Failed` and is never retried. `InfraError`, `Timeout` and `Cancelled` are excluded from the graded pass rate and mean score; `Timeout` counts as a failure in the end-to-end rate (see Metrics).
- Cancellation:
  - Ctrl+C, the web cancel button, or `run cancel <id>` stops queuing and marks in-flight attempts `Cancelled`. The run becomes `Cancelled`.
  - `run cancel` (and the web button, besides its in-process token) sets `Run.CancelRequested`. The executor polls it every 2 s and cancels its own token, so it works from another process. The flag is cleared when an executor starts.
- Run lease (MB-010): `ExecuteAsync` first takes `Run.LeaseOwner`/`Run.LeaseExpiresMs` with one conditional `UPDATE` (free, or expired), before it touches any attempt. A second executor in any process gets `RunLeaseException` and changes nothing. The cancel poller renews the lease every tick (TTL 30 s, or 3 poll intervals if longer); if renewal finds another owner, the executor stops and does not write the run status. The lease is released when the executor finishes. A crashed executor's lease expires, so its run can be resumed after at most the TTL.
- The dashboard offers Resume for Cancelled, Failed and Running runs, and for Pending runs that `RunHostService` is not tracking (`RunHostService.IsTracked`; its queue is in memory only) (`RunHostService.Resume`: `PrepareResume`, then `ExecuteAsync(retryInfra)`); a refusal message is shown on the page. A lease refusal is logged and leaves the run's status alone.
- Resume (`run resume <id> [--retry-infra]`):
  - It re-executes missing, `Pending` and `Cancelled` attempts. A run left `Running` by a crashed process resumes the same way once its lease has expired. A run that another process is executing is refused (`PrepareResume` checks the lease early for a clear message; the atomic acquisition in `ExecuteAsync` is what guarantees it).
  - With `--retry-infra`, `InfraError` and `Timeout` rows are deleted and redone. The old row's `Retries` is added to the new attempt's `Retries` (the cumulative count of retries across executions).
  - `RunService.PrepareResume` first checks that the suite exists and that its task hashes and `Suite.ContentHash` still match. It then re-resolves the model by `Run.ModelRef` and compares its snapshot with `Run.ModelSnapshotJson`. A difference is always refused with a per-field diff (MB-005): every attempt of a run shares one model configuration, so a changed configuration needs a new run.
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
| `performance` (GradingPipeline forces Weight 0 / Required false for every performance spec, whatever the stored spec says) | `{ language, entryPoint, args: [..], expected: json, budgetMs: number }`. Runs the case alone with the sandbox `TimeoutMs = budgetMs`. Within-budget means a **correct** output (MB-003), not killed, and **host** wall time within the budget; in-container timings are informational only, because candidate code can forge them. Host wall time depends on load, so the verdict is empirical: `code-generation` emits this grader with weight 0, not required (MB-002), and regrade skips unscored assertions | `correct`, `within-budget` |
| `ast` | `{ language, rules: [{ kind: "defines"\|"forbids-import"\|"forbids-call"\|"requires-loop"\|"forbids-recursion", name? }] }` | one per rule |
| `tool-calls` | `{ required: [{ name, args?: json-subset }], ordered?: bool, forbidden?: [name], maxCalls?: int }`. `called[i]` and `order` credit only **executed** calls (not on the transcript's last message, which the step limit leaves unexecuted, and arguments within 64 KB; the same rule as regrade's replay). `forbidden` and `max-calls` count every **requested** call (MB-008) | `called[i]:name` per requirement, `order`, `forbidden:name`, `max-calls` |
| `env-state` | `{ expected: json-subset }`, flattened to leaf paths (object keys joined by `.`, array items as `[i]`) and compared by deep equality with the final environment state; `{"path": null}` asserts absence; a null state fails every assertion | one per leaf path |
| `canary` | `{ canaries: [string], forbiddenTools?: [name] }`. Scans the final text, every assistant message and every tool-call argument (never tool results or user/system messages). Canary and haystack are normalised to lower-case letters and digits only; homoglyphs are not detected | `no-canary:<first 8 chars>…` per canary, `not-called:<tool>` per forbidden tool |

The `unit-tests`, `differential` and `performance` graders send `SandboxRequest(code, entryPoint, inputs)`. They compare the returned outputs with the expected values **on the host**.

Every grader compares JSON numbers with `CodeSupport.NumberEqual` (MB-007). With zero tolerance the comparison is exact: integers as `BigInteger`, otherwise `decimal` (so `1`, `1.0` and `1e0` are equal), and `double` only beyond decimal's range. A positive tolerance is an absolute difference in `double`.

`GradingPipeline.Version` names the grading methodology and is stored on every run (`Run.GradingVersion`; runs that predate it are v1). Bump it whenever a change can move an existing attempt's score or outcome. The leaderboard groups by (suite hash, grading version), `compare` refuses runs with different versions, and `regrade` refuses a run graded with another version.

`modelbench run regrade <id> [--check]` (`Execution/Regrader.cs`) re-runs the pipeline on every stored Passed/Failed attempt from its stored transcript plus the task's PromptJson/HiddenJson. For tool and agentic attempts the final environment state is rebuilt by replaying the transcript's tool calls into a fresh `family.CreateEnvironment(hidden)`. It compares assertions (grader, name, expected, actual, score, passed), the attempt score and the outcome, reports differences, and never writes. Unscored assertions (weight 0, not required; the performance grader's) are skipped, because host wall time is not reproducible. With `--check` it exits non-zero on any difference (AC-01). Code families need the sandbox.

## Sandbox (ADR 0003)

`DockerSandbox` drives the `docker` CLI and talks to the in-container runner over a simple protocol:
- It pipes the request JSON (code, entryPoint, inputs, action, repetitions, nonce) to the container's **stdin** (`docker run -i`). Nothing is mounted. The runner reads stdin fully before loading candidate code.
- The runner never executes candidate code itself (#3). Each case runs in a fresh child process (Python `fork`, C# `Runner.dll --case`) that returns `{output, error, elapsedMs}` on a private pipe, with every other descriptor on `/dev/null`. The runner checks each child's exit status (`case process exited with status N`, `killed by signal N`, `produced no result`), assigns the index itself, then kills and reaps leftover processes and empties `/tmp`, `/dev/shm` and `/dev/mqueue` and removes SysV IPC objects before the next case. It is PID 1 and calls `prctl(PR_SET_DUMPABLE, 0)`, so a case cannot reach its stdout through `/proc/1`.
- The runner prints one line, `MBRESULT:<nonce>:<base64 json>`. The nonce is random per execution. The host requires **exactly one** such line, a container exit code of 0, and case indices `0..n-1` in order; anything else is an unusable (crashed) result, never a pass (MB-004).
- Images are `modelbench-python:3` and `modelbench-dotnet:3`. The tag changes whenever the runner protocol does, so a stale image fails as missing rather than running an old runner.
- Container flags: `--rm --network none --read-only --tmpfs /tmp:rw,size=64m --memory 256m --memory-swap 256m --cpus 1 --pids-limit 64 --cap-drop ALL --security-opt no-new-privileges --user 65534:65534 -e HOME=/tmp -i`.
- The runner kills a case at `TimeoutMs` x repetitions (that case alone errors); the host enforces a backstop wall-clock limit of `TimeoutMs` x repetitions x inputs + 5 s grace by running `docker kill`, and caps captured output at 1 MB.

## Metrics (ADR 0008)

Per attempt, summed over the episode's model calls:
- `input_tokens`, `output_tokens`, `reasoning_tokens`: Measured if the provider reported them, otherwise Unavailable.
- `ttft_ms`: Measured from the first model call.
- `duration_ms`: Measured, inference wall time of the whole attempt: from the first try to the end of the successful episode, including failed tries and back-off (MB-009).
- `throughput_tps`: Calculated as output tokens / (total − ttft) for the first call, and only when both are measured.
- `model_calls`: Measured.
- `duration_ms` of an attempt that replaced a deleted one (resume) covers only the replacement, not the earlier attempt. Per-request telemetry persistence (keeping the usage of failed tries and replaced attempts) is deferred; until then incomplete usage is reported Unavailable.
- When usage is incomplete (the attempt retried, or it replaced a deleted attempt via resume), the failed requests' usage was never reported, so tokens, `model_calls` and `cost_usd` are Unavailable rather than understated. Coverage (N/M) shows the gap, and cost per success is then not reported.
- `cost_usd`: Calculated from tokens × `Pricing` (reasoning tokens billed at the `Reasoning` rate if set, else at the output rate). Unavailable when tokens or pricing are missing.
- `energy_kwh` and `energy_cost_usd`: Estimated from inference time (excluding retry back-off) × `Energy.Watts` ÷ the attempts in flight (sampled at the attempt's start and end, itself included; concurrent attempts share the machine's watts) and the price.

Run level:
- **end-to-end rate**: passes / (graded + timeouts). Timeouts are the model's own budget exhaustion, so they count as failures. This ranks the leaderboard, complete runs (no infra errors or cancellations) first (MB-001); runs with completion below 1 are listed after, marked, and never outrank a complete run
- **graded pass rate** and mean score, over graded attempts only
- **completion**: (graded + timeouts) / planned attempts, where planned = mode-matching tasks x repeats (not stored rows). Below 1 means infra errors, cancellations or missing attempts hid part of the run; such a run is not authoritative: it is listed after the ranked runs and marked incomplete
- **consistency**: the fraction of tasks with the same pass/fail outcome on every repeat
- cost per successful task: Σcost / passes

## Configuration

- **Models**: `models.json`, a superset of pi's `~/.pi/agent/models.json`. The CLI looks for `--models <path>`, then `./models.json`, then `~/.modelbench/models.json`. See `models.example.json` and the user guide.
- **Database**: the `--db <path>` option, else the `MODELBENCH_DB` env var, else `~/.modelbench/modelbench.db`.
- **Secrets**: `apiKey: "{env:NAME}"` is resolved at request time.
