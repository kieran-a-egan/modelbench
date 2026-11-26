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
- The interactive actions are covered by the browser check below.

### Dashboard (browser check, Playwright + Chromium)
The setup:
- Playwright 1.x drove headless Chromium against `dotnet run --project src/ModelBench.Web`.
- The app used a fresh scratch DB and a **copy** of the pi models file.
- The live Strata model served the runs.
- Every step asserted on what the page rendered. One clean pass, all checks green, 0 browser console errors.

Models:
- The table lists the Strata model.
- Saving invalid JSON shows the parser error and leaves the file byte-identical.
- A valid save, which added an `energy` profile, re-renders the table ("350 W") and writes the file; `apiKey` is unchanged.
- The pi file has no literal keys (env/none only), so masking of literal keys rests on `SecretHandlingTests`.

Suites:
- Generate creates `ui-small` (4 tasks) and `ui-cancel` (10 tasks).
- Verify reports "verified, every task and suite hash regenerates identically".
- Submitting with no family selected shows "select at least one family".

Runs:
- Start run (model, variant `low` from the pi `thinkingLevelMap`, concurrency, label) navigates to the run page.
- The progress line updates live without a reload (done counts 0→1→2→3→4), and on completion the page reloads into the summary, attempts and failures.
- **Cancel:** the run stopped at 2/10, showing "Passed 1, Cancelled 1", and the Resume panel appeared.
- **Resume:** the run completed 10/10.

Run and attempt pages:
- The run page's JSON and the three CSV export links download with 200.
- The attempt page shows the prompt, assertions, metrics, transcript and raw request/response.

Reports:
- The leaderboard ranking and breakdown views work: per-category and per-complexity percentages, grouped by suite hash.
- The model link opens History.
- The Compare form (A = `@low`, B = no variant, same suite) shows the summary deltas and the per-task table.

Found and fixed by this check (not visible to HTTP-only checks):
- Every page's `<h1>` showed a heavy focus outline after Blazor's FocusOnNavigate. Fixed with `h1:focus { outline: none; }`; the heading is only programmatically focusable.
- Web metric tables printed raw doubles (`323.5002`). `Fmt.Num` now uses the CLI's magnitude rule.

### Not verified live
- The OpenAI Responses, Anthropic Messages and Google adapters were tested only against fixture SSE payloads; no API keys were available.
  - To check them for real, set `OPENAI_API_KEY`, `ANTHROPIC_API_KEY` or `GEMINI_API_KEY` and run `tests/ModelBench.Tests/bin/Debug/net10.0/ModelBench.Tests.exe -class "*LiveSmokeTests"`.

## 2026-11-25: scope change, local models only

The hosted-API adapters (OpenAI Responses, Anthropic Messages, Google Generative AI), their fixture tests and the opt-in `LiveSmokeTests` were removed: ModelBench benchmarks local models only. The two AC-13 entries above ("◐ remote APIs: fixtures only", "3 skips are LiveSmokeTests") describe the code before this change. AC-13 now covers `openai-completions` and manual, verified by the tests and the live Strata runs recorded above.
