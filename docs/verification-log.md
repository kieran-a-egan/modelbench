# Verification log

Manual and live checks backing `docs/acceptance-criteria.md`. Automated evidence is the test suite.

## 2026-11-25: full verification (commit after "Performance assertion actual is the verdict")

**Environment:** Windows 11, .NET SDK 10.0.400, Docker 29.6.2, sandbox images `modelbench-python:1` and `modelbench-dotnet:1`.

### Automated
- `dotnet build -warnaserror`: 0 warnings, 0 errors.
- `dotnet test`: **458 total, 455 passed, 0 failed, 3 skipped**.
  - The 3 skips are `LiveSmokeTests` for OpenAI, Anthropic and Google. They skip because no API keys are set.
  - Docker-dependent tests ran for real: the sandbox, the adversarial sandbox suite, the code families, and regrade.
- `modelbench selftest` (CLI, real DockerSandbox): **passed, 1220 checks**. Every family's reference turns score 1.0, and every known-bad turn fails, at complexities 1..5.

### Live model: Strata Desktop `inkling-2-mini-sq-15`
The model was configured with `--models ~/.pi/agent/models.json`, the user's unmodified pi file.

| Run | Suite | Mode | Result |
|---|---|---|---|
| `@low` | `hard-mixed` (14 tasks, 11 families, complexity 4–5) | SingleTurn | 8/14 passed, mean score 0.727 |
| `@high` | same suite (identical instances) | SingleTurn | 12/14 passed, mean score 0.921 |
| `@low` | `agentic` (agentic-task-completion ×2) | Agentic | 2/2 |
| `@low` | same suite (adversarial-tools, tool-calling c5) | ToolAssisted | 2/2 |
| `@low` | `perf` (code-generation c5 ×3, Docker sandbox + performance budget) | SingleTurn | 3/3 |

Earlier smoke runs: mathematics 4/4; tool-calling 3/3; code-generation, debugging and deductive-reasoning 4/4.

Checks performed:
- **Failure inspection** (`run show --failures`): each failed assertion shows its expected value, actual value and diagnostic. Examples:
  - mathematics: "expected 182469 but got 183469 (difference 1000)"
  - deductive-reasoning: per-cell mismatches
  - constraint-satisfaction: `edge[1,11]` violated, so `optimal` cannot be met
- **Compare** (`compare low high`): same suite hash. 5 FailToPass, 1 PassToFail, 7 BothPass, 1 BothFail, plus token, ttft, throughput and duration deltas.
- **Leaderboard** (`leaderboard --mode SingleTurn`): grouped by suite hash; `@high` ranks above `@low`.
- **Metrics provenance:**
  - Tokens, including reasoning tokens, are Measured with 14/14 coverage.
  - Cost is Calculated, and is 0 because the pi file prices the model at 0.
  - Energy is **Unavailable** (0/14), because the pi file has no `energy` profile. It was not fabricated.
- **Regrade determinism** (`run regrade --check`):
  - Agentic and ToolAssisted runs: 0 differences.
  - The new code-generation run with performance budgets: 0 differences.
  - The two runs graded before the performance-assertion fix differed only in the recorded wall-time text of `within-budget`; the verdict and score were identical. This led to the fix that moved timing into the diagnostic.

### Dashboard (HTTP check)
- I started `dotnet run --project src/ModelBench.Web` against the live DB with `MODELBENCH_MODELS` set to the pi file.
- These all returned 200 with real data: `/`, `/models`, `/suites`, `/runs`, `/runs/{id}`, `/attempts/{id}`, `/leaderboard` (SingleTurn and Agentic), `/compare?a=&b=` (shows FailToPass/PassToFail rows), `/export/runs/{id}.json` and `/export/runs/{id}/assertions.csv`.
- On the attempt page, failed assertions render with HTML-encoded expected and actual values.
- The Models page contains no API key values.
- **Not verified in a browser:** no browser automation was available in this session, so the interactive Blazor actions were not exercised by clicking. Those actions are model save, suite generate/verify, run start/cancel/resume and progress polling. Their underlying services are covered by tests, and the pages' server-rendered output is covered by `WebSmokeTests`.

### Not verified live
- The OpenAI Responses, Anthropic Messages and Google adapters were tested only against fixture SSE payloads; no API keys were available.
  - To check them for real, set `OPENAI_API_KEY`, `ANTHROPIC_API_KEY` or `GEMINI_API_KEY` and run `tests/ModelBench.Tests/bin/Debug/net10.0/ModelBench.Tests.exe -class "*LiveSmokeTests"`.
