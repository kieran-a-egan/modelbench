# Acceptance criteria and requirements traceability

Status legend:
- ☐ not started
- ◐ implemented, not verified
- ☑ verified, with evidence in the Evidence column

Evidence is either a test name (`Class.Method`) or a recorded manual check in `docs/verification-log.md`.

| AC | Requirement (spec section) | Criterion | Code | Evidence | Status |
|---|---|---|---|---|---|
| AC-01 | Deterministic grading | Re-grading every stored attempt reproduces identical assertions and scores. No grader references `IModelProvider` or `HttpClient`. | Grading/* | `RegradeDeterminismTests`, `ArchitectureTests.GradersHaveNoNetwork` | ☐ |
| AC-02 | No LLM judge | Only `Providers/*` performs model HTTP calls. Grading never calls a provider. | Providers, Grading | `ArchitectureTests` | ☐ |
| AC-03 | Inspectable assertions | Attempt score equals the weighted mean of its stored assertions. Every assertion stores expected, actual, score and diagnostic. | GradingPipeline | `GradingPipelineTests` | ☐ |
| AC-04 | Established before execution | A run can only be created for a persisted suite. Task instances have no update path. | RunService | `RunServiceTests.RequiresPersistedSuite` | ☐ |
| AC-05 | Reproducible instances | The same spec and seed produce identical suite hashes. `suite verify` passes. Golden hash per family. | SuiteGenerator | `ReproducibilityTests` | ☐ |
| AC-06 | Hidden data not in prompts | Hidden markers never appear in serialized provider requests (all families, all adapters). | EpisodeRunner, adapters | `HiddenIsolationTests` | ☐ |
| AC-07 | Hidden data not in sandbox | The sandbox request carries no expected values. Nothing is mounted into the container; the request is piped via stdin. An adversarial scan of the container filesystem finds no expected outputs. | DockerSandbox | `HiddenIsolationTests`, `SandboxAdversarialTests` | ☐ |
| AC-08 | Sandbox isolation | No network, read-only rootfs, non-root, memory/pid/cpu limits, timeout and output cap are all enforced. | DockerSandbox, sandbox/* | `SandboxAdversarialTests` | ☐ |
| AC-09 | No fabricated telemetry | Missing usage becomes an Unavailable metric. Every metric has a provenance. | Metrics | `MetricsTests` | ☐ |
| AC-10 | Generators validated | All 13 families: seeded, complexity 1..5, independent `Validate`. Selftest: reference turns score 1.0 and every known-bad turn fails a required assertion. | Families/*, SelfTest | `SelfTestTests` (all families × complexities) | ☐ |
| AC-11 | Static + repository definitions | `benchmarks/static` and `benchmarks/repos` definitions load, generate, run and grade. | Families/Static* | `StaticDefinitionTests` | ☐ |
| AC-12 | Grader coverage | All 14 grader types exist, each with pass and fail tests and assertion-level output. | Grading/* | `Graders/*Tests` | ☐ |
| AC-13 | Provider adapters | The openai-completions, openai-responses, anthropic-messages, google-generative-ai and manual adapters each handle text, tool calls, usage incl. reasoning, streaming time to first token, and error→InfraException mapping. | Providers/* | `Providers/*Tests` (fixtures), live smoke (opt-in) | ☐ |
| AC-14 | Model configuration | pi-compatible `models.json` (the real `~/.pi` shape loads), `{env:X}`, variants and thinkingLevelMap work. The snapshot strips secrets. | Config/ModelCatalog | `ModelCatalogTests` | ☐ |
| AC-15 | Execution modes | Single-turn, tool-assisted, agentic and manual each run end to end. | Execution/* | `ExecutionModeTests`, manual round trip | ☐ |
| AC-16 | Execution controls | Repeats, concurrency, per-attempt timeout, retry (infra only), cancellation and resume all work. | RunExecutor | `RunExecutorTests` | ☐ |
| AC-17 | Infra vs model failure | Infra failures become InfraError, are excluded from scores, and are shown separately. Bad answers are never retried. | RunExecutor, Reporting | `RunExecutorTests.FailureClassification` | ☐ |
| AC-18 | Metrics | Tokens, reasoning tokens, time to first token, throughput, duration, cost (Calculated), energy (Estimated), consistency and cost per success are all recorded or reported. | Metrics, Reporting | `MetricsTests`, `ReportingTests` | ☐ |
| AC-19 | Recorded settings | A run stores the model snapshot (model id, sampling, reasoning variant, context limit, compat), the environment fingerprint and the retry policy. | RunService | `RunServiceTests.SnapshotsSettings` | ☐ |
| AC-20 | CLI | The CLI covers models, suites, runs (start/resume/cancel/show/list), manual, leaderboard, compare, export, sandbox and selftest. | Cli | `CliTests` | ☐ |
| AC-21 | Dashboard | The dashboard has Models, Suites, Runs (start/progress/cancel), a leaderboard per mode, category and difficulty breakdowns, failures, raw prompt/response, compare, cost per success, and exports. | Web | `WebSmokeTests` + browser check | ☐ |
| AC-22 | Exports | CSV and JSON exports of runs, attempts and assertions, from both CLI and Web. | Reporting | `ExportTests` | ☐ |
| AC-23 | Untrusted rendering | No `MarkupString` on untrusted content. CLI output is sanitized. | Web, Cli | `UiSafetyTests`, `ConsoleSafeTests` | ☐ |
| AC-24 | Secrets | API keys never appear in the DB, logs or exports. | Config, Providers | `SecretHandlingTests` | ☐ |
| AC-25 | Documentation | Architecture, ADRs, threat model, user guide, this matrix and the verification log all exist and match the code. | docs/ | review | ☐ |
| AC-26 | Definition of done | Configure model → generate suite → run → scores → inspect failed assertions → compare with previous run. Done both against the fake server (test) and the live Strata model (manual). | all | `EndToEndTests`, verification log | ☐ |

## Spec → AC map
- **Non-negotiables**
  - deterministic grading: AC-01, AC-03
  - no LLM judge: AC-02
  - criteria established before execution: AC-04
  - seeds and versions recorded: AC-05
  - hidden data never exposed: AC-06, AC-07
  - no host execution of candidate code: AC-08
  - no fabricated telemetry: AC-09
  - no placeholders: AC-10, AC-12, AC-13
- **Benchmark generation**: AC-05, AC-10, AC-11
- **Grading**: AC-03, AC-12
- **Providers**: AC-13, AC-14, AC-19
- **Execution**: AC-15, AC-16, AC-17
- **Security**: AC-07, AC-08, AC-23, AC-24
- **Metrics**: AC-09, AC-18
- **User interface**: AC-20, AC-21, AC-22
- **Definition of done**: AC-26
