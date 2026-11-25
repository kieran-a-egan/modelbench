# ModelBench user guide

Commands are shown as `modelbench ...`. From the repository use `dotnet run --project src/ModelBench.Cli -- ...`. Two options apply to every command:

- `--db <path>`: the SQLite database. Default: the `MODELBENCH_DB` environment variable, then `~/.modelbench/modelbench.db`.
- `--models <path>`: the models file. Default: `./models.json`, then `~/.modelbench/models.json`.

Run, suite and attempt ids can be abbreviated to any unique prefix. Errors print `error: ...` and exit with code 1.

## models.json reference

The file is a superset of pi's `~/.pi/agent/models.json`: a pi file loads unchanged (point `--models` at it) and unknown fields are ignored. Comments and trailing commas are allowed. `models.example.json` at the repository root is a working template.

```json
{ "providers": { "<providerId>": { ...provider..., "models": [ { ...model... } ] } } }
```

A model is addressed as `providerId/modelId` or `providerId/modelId@variant`. The provider is everything before the first `/`; the variant is everything after the last `@`.

### Provider fields

| Field | Meaning |
|---|---|
| `api` | Required unless every model sets its own. One of `openai-completions`, `openai-responses`, `anthropic-messages`, `google-generative-ai`, `manual`. |
| `baseUrl` | API root, for example `http://127.0.0.1:47831/v1` or `https://api.anthropic.com`. |
| `apiKey` | A literal key, `"none"` (send no auth header), or `{env:NAME}`. |
| `headers` | Extra request headers. Values may use `{env:NAME}`. |
| `energy` | `{ "watts": 350, "pricePerKwh": 0.30 }`. Used for the energy estimate of local models. |
| `name` | Ignored by ModelBench. |
| `models` | Array of models (required). |

### Model fields

| Field | Meaning |
|---|---|
| `id` | Required. The id sent to the API. |
| `name` | Display name. |
| `api`, `baseUrl` | Override the provider's values for this model. |
| `reasoning` | `true` if the model produces reasoning. Default `false`. |
| `toolCalls` | Default `true`. |
| `contextWindow` | Tokens. Recorded in the run snapshot and listed by `models list`. |
| `maxTokens` | Output token limit sent with each request. |
| `cost` | USD per 1M tokens: `{ "input": 3, "output": 15, "reasoning": 15 }`. `reasoning` is optional; without it reasoning tokens are billed at the output rate. |
| `samplingParams` | Merged into the request body, for example `{ "temperature": 0.7, "seed": 1 }`. |
| `extra` | Raw fields merged into the request body after everything else. |
| `compat` | Wire-compatibility flags (below). |
| `variants` | Named setting sets, selected with `@name` (below). |
| `thinkingLevelMap` | pi's map, such as `{ "low": "low", "high": "high" }`. Each entry becomes a variant `{ "reasoningEffort": value }`. Explicit `variants` win on a name clash. |

Settings are merged into the request body as: `samplingParams`, then `extra` (for Anthropic and Google the variant's thinking settings are applied before them).

### Variants and thinking settings

A variant is a set of settings chosen at run time, so the same model can be benchmarked at several reasoning levels. The variant is recorded in the run snapshot and is part of the model name in leaderboards.

| Setting | Used by | Effect |
|---|---|---|
| `reasoningEffort` | openai-responses (always), openai-completions (only when `compat.supportsReasoningEffort` is true), google (as `thinkingLevel` if no `thinkingLevel`) | Sets the reasoning effort. |
| `thinking: "adaptive"` | anthropic-messages | Adaptive thinking with summarised display. |
| `thinkingBudget` | anthropic-messages, google-generative-ai | Token budget for thinking. |
| `thinkingLevel` | google-generative-ai | Thinking level. |

### Compat flags

| Flag | Applies to | Meaning |
|---|---|---|
| `maxTokensField` | openai-completions | Name of the output limit field. Default `max_completion_tokens`; local servers often need `max_tokens`. |
| `supportsReasoningEffort` | openai-completions | Send `reasoning_effort` from the variant. |
| `anthropicVersion` | anthropic-messages | The `anthropic-version` header. Default `2026-10-21`. |
| `anthropicBeta` | anthropic-messages | The `anthropic-beta` header. |

**Anthropic sampling note.** With `anthropic-version` 2026-10-21 or later (the default) the API rejects `temperature`, `top_p` and `top_k` with HTTP 400. ModelBench therefore refuses to start with a clear message if those appear in `samplingParams`; remove them, or pin an older `anthropicVersion` if you need them.

### Secrets

`{env:NAME}` in `apiKey` or a header value is replaced from the environment when a model is resolved (at `run start` and `run resume`). A missing variable is an error that names it. Keys are held in memory only: the run snapshot stores the model without `apiKey` and with every header value replaced by `***`, raw requests contain bodies only, and exports contain neither (verified by `SecretHandlingTests`). A literal key in `models.json` is as safe as that file; prefer `{env:...}`. The dashboard's model editor masks literal keys and header values.

### Energy and pricing

- `cost` turns the provider's reported token counts into `cost_usd` (provenance Calculated). If the provider reports no tokens, or no `cost` is configured, cost is Unavailable.
- `energy` gives `energy_kwh = duration x watts` and `energy_cost_usd = kWh x pricePerKwh`, both provenance **Estimated**. It is a wall-clock estimate, not a measurement.

### API kinds

| `api` | Notes |
|---|---|
| `openai-completions` | `/chat/completions`, streaming. Works with most local servers. Bearer auth unless the key is `none`. |
| `openai-responses` | OpenAI Responses API. |
| `anthropic-messages` | Messages API, `x-api-key` auth. Thinking blocks are replayed in tool loops. |
| `google-generative-ai` | Gemini `generateContent`. |
| `manual` | No network. Used for the manual workflow. |

`modelbench models list` prints every model, its api, reasoning flag, context window and variants.

## CLI commands

### Suites

```
modelbench suite generate --name first --seed 1 --family mathematics:all:5 --family debugging:3:2
modelbench suite list
modelbench suite show <suite>
modelbench suite verify <suite>
```

`--family id[:complexity[:count]]` is repeatable. Complexity is 1 to 5 or `all` (one entry per level); the default is all levels. Count defaults to 5. `--seed` is the master seed. Static and repository families use ids `static:<name>` and `repo:<name>`. `suite verify` regenerates the suite from its stored spec and compares every hash.

### Runs

```
modelbench run start --suite <suite> --model strata/inkling-2-mini-sq-15@high [options]
modelbench run list
modelbench run show <run> [--failures]
modelbench run cancel <run>
modelbench run resume <run> [--retry-infra] [--allow-config-change]
modelbench run regrade <run> [--check]
```

`run start` options: `--mode SingleTurn|ToolAssisted|Agentic|Manual` (default SingleTurn), `--repeats N` (attempts per task), `--timeout SECONDS` (per attempt, default 300), `--concurrency N`, `--max-retries N` (retries per attempt on retryable infrastructure errors), `--label TEXT`. A run executes only the suite tasks whose mode matches the run mode, so a mixed suite is benchmarked with one run per mode. Ctrl+C cancels cleanly and the run can be resumed. `run start` exits 0 only if the run completed.

`run cancel` asks the process executing the run (this or another one) to stop; it polls the flag every two seconds.

`run resume` re-executes missing, pending and cancelled attempts; `--retry-infra` also redoes InfraError and Timeout attempts (their retry counts accumulate). It refuses if the suite no longer matches its hashes, or if the model's configuration differs from the run's snapshot (a per-field diff is printed) unless `--allow-config-change` is given, which marks the run label `[config changed on resume]`. Do not resume a run that is still executing in another process.

`run regrade` is described under Reproducibility.

### Reports

```
modelbench leaderboard [--mode ToolAssisted] [--suite <suite>] [--all-runs]
modelbench breakdown --suite <suite> [--mode SingleTurn]
modelbench compare <runA> <runB>
modelbench history --model strata/inkling-2-mini-sq-15@high --suite <suite>
modelbench export --run <run> --format json --out run.json
modelbench export --run <run> --format csv --out ./run-csv
```

Leaderboards are separate per execution mode and group runs by suite **content hash**, so equal suites generated twice rank together. By default only the latest completed run per model and suite is listed; `--all-runs` shows all. `compare` refuses runs on different suite contents. CSV export writes `runs.csv`, `attempts.csv` and `assertions.csv`.

### Manual, selftest, sandbox

```
modelbench manual export --suite <suite> --model manual/chat-ui --out ./prompts [--repeats N]
modelbench manual import --run <run> --file responses.jsonl
modelbench selftest [--family mathematics]
modelbench sandbox build [--dir ./sandbox]
modelbench sandbox check
```

`selftest` replays every family's reference answer (must score 1.0) and known-bad answers (must fail) through the real pipeline. Run it after changing a family or grader.

## Execution modes

| Mode | What happens | Graded on |
|---|---|---|
| SingleTurn | One model call with `[system?, user]`. | Final answer. |
| ToolAssisted | The model gets the task's tools; calls run against a deterministic simulated environment until the model answers without tool calls or 8 model calls are made. | Final answer plus the tool calls made. |
| Agentic | The same loop with up to 30 model calls. | Mainly the final environment state. |
| Manual | You paste prompts into any chat UI and import the responses. Telemetry is all Unavailable. | Final answer, as SingleTurn. |

A run has exactly one mode and leaderboards never mix modes. Tool environments are simulations: no real files, network or clock are touched.

## Manual workflow

1. `modelbench manual export --suite <suite> --model manual/chat-ui --out ./prompts` creates a Manual run and writes `prompts.jsonl` (`{runId, taskId, repeat, system, user, answerFormat}` per line) and a human-readable `prompts.md`.
2. Paste each prompt into the chat UI.
3. Write `responses.jsonl`, one object per line: `{"taskId": "...", "repeat": 0, "response": "<answer>42</answer>"}`.
4. `modelbench manual import --run <run> --file responses.jsonl` grades each response exactly like a single-turn attempt.

The `manual` provider entry in `models.json` needs no URL or key.

## Reading results

`run show` prints, in order:

- **Attempt outcomes**: `Passed`, `Failed` (a model failure: wrong or malformed answer, never retried), `InfraError` (provider or sandbox failure after retries), `Timeout`, `Cancelled`, `Pending`. InfraError, Timeout and Cancelled are **excluded** from pass rate, mean score and consistency, and are counted separately so a flaky server cannot masquerade as a weak model.
- **Pass rate and mean score.** An attempt passes when every required assertion passes. Its score is the weighted mean of its assertion scores, so a partially right answer scores between 0 and 1. The `answer-format` assertion is required but weighs 0: it can fail an attempt without inflating a wrong answer's score.
- **Consistency**: the fraction of tasks with the same pass/fail result on every repeat. It only means something with `--repeats` of 2 or more.
- **Slices** by family, category and complexity.
- **Metrics** with coverage (how many attempts have the value): input, output and reasoning tokens, cost, estimated energy.
- **Cost per success**: total cost divided by the number of passes.

`--failures` lists every failed assertion with its expected value, actual value and a diagnostic. The dashboard shows the same plus the raw prompts and responses of each attempt.

### Provenance

Every metric states where it came from:

| Provenance | Meaning |
|---|---|
| Measured | Reported by the provider or timed by ModelBench (tokens, time to first token, duration, model calls). |
| Calculated | Derived exactly from measured values (cost, throughput). |
| Estimated | Derived with an assumption (energy: duration x configured watts). |
| Unavailable | Not reported. Never guessed. A leaderboard cell for it is empty or marked n/a. |

Time to first token needs a streaming provider; manual runs have no telemetry at all.

## Defining your own benchmarks

Definitions are trusted input that you author. They load at start-up from the nearest `benchmarks/` directory above the executable (override with `MODELBENCH_BENCHMARKS`). Task *i* of a suite entry is definition task *i* modulo the number of tasks; the seed and spec complexity are ignored, and the file's own `complexity` is recorded. A definition's `version` is the integer in the file; its content hash (the JSON plus prompt and repository files) is stored in each task's hidden `Reference`, so editing a definition changes the suite hash even if you forget to bump `version`. Run `selftest --family static:<id>` to check your reference and known-bad answers.

### Static definitions: `benchmarks/static/<file>.json`

```json
{
  "id": "unit-conversion",
  "version": 1,
  "category": "mathematics",
  "mode": "SingleTurn",
  "complexity": 1,
  "description": "Four unit conversions with exact numeric answers.",
  "tasks": [
    {
      "promptFile": "unit-conversion.q1.md",
      "system": "optional system prompt",
      "answerFormat": "Tagged",
      "graders": [{ "type": "numeric", "name": "value", "config": { "expected": "8.89" }, "weight": 1, "required": true }],
      "reference": "3.5 x 2.54 = 8.89\n<answer>8.89</answer>",
      "knownBad": ["<answer>8.9</answer>", "The result is 8.89 cm."]
    }
  ]
}
```

The family id becomes `static:<id>`. Each task has `prompt` (inline) or `promptFile` (Markdown beside the JSON). `answerFormat` is `Tagged`, `CodeBlock`, `Json` or `FreeText`; the prompt must tell the model which to use. `graders` use the grader types listed in `docs/architecture.md`; `weight` defaults to 1 and `required` to true. `reference` is a perfect answer and `knownBad` lists wrong answers; if you give a reference you must give at least one known-bad answer. `mode` is `SingleTurn` (the default); definitions cannot declare tools.

### Repository definitions: `benchmarks/repos/<name>/benchmark.json`

```
benchmarks/repos/inventory-service/
  benchmark.json
  repo/inventory/stock.py ...
```

```json
{
  "version": 1,
  "category": "repository-comprehension",
  "mode": "SingleTurn",
  "complexity": 2,
  "description": "Four questions about a small inventory service.",
  "include": ["inventory/*.py"],
  "maxBytes": 8000,
  "questions": [
    {
      "question": "Question: in which file is `is_low` defined? Answer inside <answer></answer> tags.",
      "answerFormat": "Tagged",
      "graders": [{ "type": "exact", "name": "path", "config": { "expected": "inventory/stock.py" } }],
      "reference": "<answer>inventory/stock.py</answer>",
      "knownBad": ["<answer>stock.py</answer>"]
    }
  ]
}
```

The family id is `repo:<directory name>` (there is no `id` field). Every file under `repo/` matching an `include` glob (`*`, `**`, `?`; default `**/*`) is shown in each prompt in path order, with a header `### path`; loading fails if the files exceed `maxBytes` (default 60000). Each question is appended to that text. Questions take the same `answerFormat`, `graders`, `reference` and `knownBad` as static tasks.

## The sandbox and its limits

Code families (code-generation, debugging) run candidate Python and C# in Docker. Build the images once with `modelbench sandbox build`; `sandbox check` prints their ids and the Docker version. Each execution is a fresh container with:

- no network, a read-only root file system, a 64 MB `/tmp`, and nothing mounted (the request is piped through stdin),
- a non-root user, all capabilities dropped, `no-new-privileges`,
- 256 MB memory (no swap), 1 CPU, at most 64 processes,
- a host-enforced wall-clock limit (the case timeout plus 5 s grace, then `docker kill`) and a 1 MB output cap.

Test inputs go in; expected outputs never do. The container returns outputs and the host compares them. Performance budgets use the host's clock and include container start-up, so they depend on machine load; run benchmarks that use them on a quiet machine. If Docker is missing or the images are not built, code attempts become `InfraError` (and are excluded from scores) rather than failing the model. Non-code families do not need Docker.

## Reproducibility

- **Seeds and versions.** A suite stores its master seed, the spec, each family's version at generation time, and a SHA-256 content hash per task and for the suite. Each task's seed derives from (master seed, family id, family version, index). Bump a family's version whenever its output changes.
- **`suite verify <suite>`** regenerates every task and compares hashes; it fails if a generator changed. Suites are frozen: tasks cannot be edited after creation.
- **Run snapshots.** A run stores the model settings (without secrets), the retry policy and an environment fingerprint (OS, .NET, Docker version, sandbox image ids, git SHA).
- **`run regrade <run> [--check]`** re-runs the grading pipeline on every stored Passed/Failed attempt from its stored transcript and the task's hidden data. For tool and agentic attempts the final environment state is rebuilt by replaying the transcript's tool calls into a fresh environment. It reports every attempt whose assertions (grader, name, expected, actual, score, passed), score or outcome differ from the stored ones. It never modifies the database. With `--check` the exit code is 1 if anything differs, which makes it usable in CI. Code families need the sandbox.
- **Comparisons** are only valid between runs on the same suite content hash.

Because graders never call a model, a differing regrade always means a grader, family or sandbox changed, or the database was edited.

## Dashboard

`dotnet run --project src/ModelBench.Web` serves the dashboard (the URL is printed; set `MODELBENCH_DB` and `MODELBENCH_MODELS` to choose files). It has Models (with a masked editor for `models.json`), Suites, Runs (start, progress, cancel), per-mode leaderboards, breakdowns, history, compare, task and attempt detail with raw prompts and responses, and CSV/JSON exports. On a run's page, **Resume** appears for Cancelled, Failed and Running runs, with a checkbox to retry infrastructure errors; if the suite or model configuration changed, the refusal is shown on the page (use the CLI's `--allow-config-change` to override). The dashboard executes runs one at a time in the background of the web process.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `no models.json found` | Pass `--models`, or create `./models.json` or `~/.modelbench/models.json`. |
| `environment variable X is not set` | A `{env:X}` reference in `apiKey` or `headers`. Set it in the shell that runs ModelBench. |
| `anthropic-version ... rejects 'temperature'` | Remove `temperature`, `top_p`, `top_k` from `samplingParams` of an Anthropic model, or set `compat.anthropicVersion` older. |
| Many `InfraError` attempts | The endpoint is down, rate-limited (HTTP 429/5xx) or timed out. Retries use backoff and `Retry-After`. Fix the cause and `run resume <run> --retry-infra`. |
| Code attempts are `InfraError` | Docker is not running or images are missing. Start Docker and `modelbench sandbox build`. |
| `suite has no ToolAssisted tasks` | The run `--mode` does not match the suite's task modes. Generate a suite with families of that mode. |
| `model configuration ... changed since the run started` | `models.json` differs from the run's snapshot. Revert it, or `--allow-config-change`. |
| `suite ... no longer matches its content hash` | The suite rows were modified; the run cannot be trusted or resumed. |
| `database ... predates schema migrations` | An old database file; delete or rename it. |
| Leaderboard lacks a run | Only Completed runs appear, grouped per mode and suite hash; use `--mode` and `--all-runs`. |
| `compare` refuses two runs | They used different suite contents. Re-run both on the same suite. |
| `regrade --check` reports differences | A grader, family or sandbox changed since the run, or the DB was edited. The diff shows the first differing assertion. |
| Port or reconnect problems in the dashboard | Check the printed URL; the dashboard keeps no state outside the database, so restarting is safe. A run executing when the process stops stays `Running`; resume it. |
