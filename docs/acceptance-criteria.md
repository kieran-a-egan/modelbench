# Acceptance criteria and requirements traceability

Status legend:
- ☐ not started
- ◐ implemented, not verified
- ☑ verified, with evidence in the Evidence column

Evidence is either a test name (`Class.Method`) or a recorded manual check in `docs/verification-log.md`.

| AC | Requirement (spec section) | Criterion | Code | Evidence | Status |
|---|---|---|---|---|---|
| AC-01 | Deterministic grading | Re-grading every stored attempt reproduces identical assertions and scores. No grader references `IModelProvider` or `HttpClient`. | Grading/* | `RegradeDeterminismTests`, `ArchitectureTests`; live `run regrade --check` (verification log) | ☑ |
| AC-02 | No LLM judge | Only `Providers/*` performs model HTTP calls. Grading never calls a provider. | Providers, Grading | `ArchitectureTests` (reflection + source scans) | ☑ |
| AC-03 | Inspectable assertions | Attempt score equals the weighted mean of its stored assertions. Every assertion stores expected, actual, score and diagnostic. | GradingPipeline | `GradingPipelineTests`, grader tests | ☑ |
| AC-04 | Established before execution | A run can only be created for a persisted suite. Task instances have no update path. | RunService | `RunServiceTests` | ☑ |
| AC-05 | Reproducible instances | The same spec and seed produce identical suite hashes. `suite verify` passes. Golden hash per family. | SuiteGenerator | `ReproducibilityTests`, golden hashes in family tests, `PlatformIndependenceTests` | ☑ |
| AC-06 | Hidden data not in prompts | Hidden markers never appear in serialized provider requests (all families, all adapters). | EpisodeRunner, adapters | `HiddenIsolationTests` + per-family isolation tests (code, tool-calling, Phase 5 families) | ☑ |
| AC-07 | Hidden data not in sandbox | The sandbox request carries no expected values. Nothing is mounted into the container; the request is piped via stdin. An adversarial scan of the container filesystem finds no expected outputs. | DockerSandbox | `CodeGraderTests` (BuildRequestJson), `SandboxAdversarialTests` | ☑ |
| AC-08 | Sandbox isolation | No network, read-only rootfs, non-root, memory/pid/cpu limits, timeout and output cap are all enforced. | DockerSandbox, sandbox/* | `SandboxAdversarialTests`, `SandboxFunctionalTests` | ☑ |
| AC-09 | No fabricated telemetry | Missing usage becomes an Unavailable metric. Every metric has a provenance. | Metrics | `MetricCalculatorTests`; live energy Unavailable (verification log) | ☑ |
| AC-10 | Generators validated | All 13 families: seeded, complexity 1..5, independent `Validate`. Selftest: reference turns score 1.0 and every known-bad turn fails a required assertion. | Families/*, SelfTest | `SelfTestTests`, per-family tests; CLI `selftest` 1220 checks (verification log) | ☑ |
| AC-11 | Static + repository definitions | `benchmarks/static` and `benchmarks/repos` definitions load, generate, run and grade. | Families/Static* | `DefinitionFamilyTests` | ☑ |
| AC-12 | Grader coverage | All 14 grader types exist, each with pass and fail tests and assertion-level output. | Grading/* | `GraderTests`, `CodeGraderTests`, `TextConstraintsGraderTests`, `JsonSchemaGraderTests`, `ConstraintGraderTests`, `ToolCallsGraderTests`, `Phase5GraderTests` | ☑ |
| AC-13 | Provider adapters (scope: local models only, 2026-11-25) | The openai-completions adapter (local OpenAI-compatible servers) and the manual workflow handle text, tool calls, usage incl. reasoning, streaming time to first token, and error→InfraException mapping. Hosted APIs (OpenAI Responses, Anthropic, Google) were removed from scope, and endpoints must be loopback or private-network. | Providers/*, Config/LocalEndpoint.cs | `OpenAiCompletionsProviderTests`, `ManualExchangeTests`, `ModelCatalogTests.OpenAiCompatibleEndpointsMustBeLocalIncludingModelOverrides`, `TransportRefusesNonLocalHostsWithoutConnecting`; live Strata runs (verification log) | ☑ |
| AC-14 | Model configuration | pi-compatible `models.json` (the real `~/.pi` shape loads), `{env:X}`, variants and thinkingLevelMap work. The snapshot strips secrets. | Config/ModelCatalog | `ModelCatalogTests`; live runs used the real `~/.pi/agent/models.json` | ☑ |
| AC-15 | Execution modes | Single-turn, tool-assisted, agentic and manual each run end to end. | Execution/* | `ToolLoopTests`, `EndToEndTests`, `ManualExchangeTests`; live SingleTurn/ToolAssisted/Agentic runs | ☑ |
| AC-16 | Execution controls | Repeats, concurrency, per-attempt timeout, retry (infra only), cancellation and resume all work. | RunExecutor | `ConcurrencyAndResumeTests`, `RunExecutorTests`, `RetryTests` | ☑ |
| AC-17 | Infra vs model failure | Infra failures become InfraError, are excluded from scores, and are shown separately. Bad answers are never retried. | RunExecutor, Reporting | `FailureClassificationTests`, `ReportingTests` | ☑ |
| AC-18 | Metrics | Tokens, reasoning tokens, time to first token, throughput, duration, cost (Calculated), energy (Estimated), consistency and cost per success are all recorded or reported. | Metrics, Reporting | `MetricCalculatorTests`, `ReportingTests`; live leaderboard/compare | ☑ |
| AC-19 | Recorded settings | A run stores the model snapshot (model id, sampling, reasoning variant, context limit, compat), the environment fingerprint and the retry policy. | RunService | `RunServiceTests` | ☑ |
| AC-20 | CLI | The CLI covers models, suites, runs (start/resume/cancel/show/list), manual, leaderboard, compare, export, sandbox and selftest. | Cli | `RunCliTests`, `ReportingCliTests`, `EndToEndTests`; live CLI session (verification log) | ☑ |
| AC-21 | Dashboard | The dashboard has Models, Suites, Runs (start/progress/cancel), a leaderboard per mode, category and difficulty breakdowns, failures, raw prompt/response, compare, cost per success, and exports. | Web | `WebSmokeTests`; Playwright browser check of every interactive action against live Strata (verification log) | ☑ |
| AC-22 | Exports | CSV and JSON exports of runs, attempts and assertions, from both CLI and Web. | Reporting | `ExportTests`, `WebSmokeTests` | ☑ |
| AC-23 | Untrusted rendering | No `MarkupString` on untrusted content. CLI output is sanitized. | Web, Cli | `UiSafetyTests`, `ConsoleSafeTests`, `WebSmokeTests` (script payload encoded) | ☑ |
| AC-24 | Secrets | API keys never appear in the DB, logs or exports. | Config, Providers | `SecretHandlingTests`, `ExportTests` | ☑ |
| AC-25 | Documentation | Architecture, ADRs, threat model, user guide, this matrix and the verification log all exist and match the code. | docs/ | README.md, docs/user-guide.md, docs/architecture.md, ADRs, threat model, this matrix, verification log | ☑ |
| AC-26 | Definition of done | Configure model → generate suite → run → scores → inspect failed assertions → compare with previous run. Done both against the fake server (test) and the live Strata model (manual). | all | `EndToEndTests` (fake server); live Strata session (verification log) | ☑ |

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
