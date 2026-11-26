# Verification log

Manual and live checks backing `docs/acceptance-criteria.md`. Automated evidence is the test suite.

## 2026-11-26: review remediation, issue #1 (MB-001 to MB-010), grading methodology v2

**Environment:** Windows 11, .NET 10, Docker running, sandbox images built. Branch `fix/review-mb-001-010` from `main` 2597e08.

### Automated
- Baseline before the changes: `dotnet test` **467 passed, 0 failed, 0 skipped**.
- After: `dotnet build -warnaserror` 0 warnings, 0 errors; `dotnet test` **490 passed, 0 failed, 0 skipped** (Docker-backed tests ran for real).

| Finding | Change | Regression evidence |
|---|---|---|
| MB-001 timeouts inflate pass rate | Leaderboard ranks by end-to-end rate, passes / (graded + timeouts); completion and graded pass rate shown separately | `ReportingTests.TimeoutsCountAgainstEndToEndRankingAndInfraErrorsShowAsIncompletion` (1 pass + 9 timeouts now ranks below 9 passes + 1 fail) |
| MB-002 wall-clock grading not reproducible | `performance` emitted with weight 0, not required; regrade skips unscored assertions; `code-generation` v3 | `RegradeDeterminismTests.HostTimingChangesNeitherTheScoreNorTheRegradeVerdict` (regrade on a host slow enough to miss the budget: 0 differences, same score); `PerformanceBudgetCalibrationTests` (each budget still separates the reference from the quadratic answer) |
| MB-003 fast wrong code gets performance credit | `within-budget` requires a correct output | `CodeGraderTests.PerformanceTimeoutFailsBothAssertionsAndWrongOutputEarnsNoPerformanceCredit` |
| MB-004 candidate shares the runner's process | Exactly one frame and in-order case indices required; residual risk documented in the threat model (decision: process-per-case isolation is a follow-up) | `RunnerIntegrityTests` (4 real-container attacks), `CodeGraderTests.MalformedCaseIndicesAreUnusableNeverAPass` |
| MB-005 resume mixes configurations | `--allow-config-change` removed; a changed configuration is always refused | `ConcurrencyAndResumeTests.ChangedModelConfigurationIsAlwaysRefusedWithADiff`, `RunCliTests.ResumeAndCancelCommands` |
| MB-006 limited prompt diversity | Capacity is stated in the exhaustion error and documented (decision: seeded spec templates are a follow-up) | `CodeFamilyTests.SuitesUpToCapacityHaveDistinctReproduciblePromptsAndBeyondItFailWithTheCapacity` |
| MB-007 integer precision | `CodeSupport.NumberEqual` (BigInteger, then decimal) used by every grader | `CodeGraderTests.ExactNumericGrading` (8 cases) |
| MB-008 unexecuted tool calls earn credit | `called`/`order` credit executed calls only; `forbidden`/`max-calls` count requested ones | `ToolCallsGraderTests.CallsRequestedAtTheStepLimitAreNeverExecutedSoEarnNoCreditButStillCountAsAttempts`, `OversizedArgumentsAreRejectedNotExecuted` |
| MB-009 retry usage understated | Duration covers all tries; usage Unavailable when incomplete | `ConcurrencyAndResumeTests.RetriedAttemptReportsUsageUnavailableAndCountsTheFailedTryInDuration` |
| MB-010 no run lease | Atomic DB lease with heartbeat and expiry (migration `RunLeaseAndGradingVersion`) | `ConcurrencyAndResumeTests.ASecondExecutorIsRefusedWhileTheLeaseIsHeldAndNoInferenceIsDuplicated`, `AnExpiredLeaseFromACrashedExecutorCanBeTakenOver` |

`selftest` consequence: the quadratic code-generation answer is no longer a known-bad case at complexity 3 and up, because it is correct and performance is no longer scored.

### Live model: Strata Desktop `inkling-2-mini-sq-15@low` (scratch database)
- Suite `e2e` (code-generation c3 ×2 with performance graders, mathematics c2 ×2, tool-calling c3 ×2), hash `cb9fe6909cea`.
- SingleTurn 4/4 and ToolAssisted 2/2 passed. Leaderboard and `run show` print end-to-end, graded pass rate, completion and `grading v2`.
- `run regrade --check`: 0 differences on both runs (exit 0).
- Lease across processes: while `run start --repeats 3` executed in one process, `run resume` from a second process was refused with "is being executed by another process" (exit 1) without changing the run. `run cancel` from a third process stopped it, and a later `run resume` completed all 12 attempts (exit 0).

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
