# ModelBench threat model

## Scope and trust boundaries

| Zone | Trust | Contents |
|---|---|---|
| Host process (Cli/Web/Core) | trusted | generators, reference solvers, graders, hidden data, API keys (in memory only) |
| SQLite DB | trusted storage, but holds **untrusted content** | candidate responses, transcripts |
| Remote/local model endpoints | semi-trusted transport, **untrusted output** | candidate text, tool calls |
| Sandbox container | **untrusted** | candidate code + test inputs |
| Browser (dashboard) | local user | renders untrusted content |

**Adversary:** the candidate model and any code it writes. A model may be compromised, or may be optimizing to game the benchmark. Treat its text, code and tool calls as hostile.

## Assets
1. Hidden expectations, hidden tests and reference solutions: benchmark integrity.
2. Grader code and behaviour: score integrity.
3. API keys and other host secrets.
4. Host filesystem, network and compute.
5. Result DB integrity, and the user's browser session.

## Threats and mitigations

| # | Threat (STRIDE) | Mitigation | Verified by |
|---|---|---|---|
| T1 | Candidate code reaches the network (exfiltration, downloads) | `--network none` | `SandboxAdversarialTests.NoNetwork` |
| T2 | Candidate code reads host files or secrets | no mounts at all (request piped via stdin); no env vars passed; non-root uid 65534 | `ReadsHostFiles`, `EnvIsEmpty` |
| T3 | Candidate code reads expected outputs to cheat | expected values never enter the container (ADR 0002); `SandboxRequest` has no field for them | `HiddenIsolationTests.SandboxRequestHasNoHiddenData` |
| T4 | Resource exhaustion (fork bomb, memory bomb, CPU spin, disk fill, giant output) | `--pids-limit 64`, `--memory 256m --memory-swap 256m`, `--cpus 1`, read-only rootfs + 64 MB tmpfs, wall-clock `docker kill`, 1 MB output cap | `ForkBomb`, `MemoryBomb`, `InfiniteLoop`, `GiantOutput`, `DiskFill` |
| T5 | Privilege escalation in the container | `--cap-drop ALL`, `no-new-privileges`, non-root, read-only rootfs | `CannotWriteRootfs`, `RunsAsNobody` |
| T6 | Forged result frames or timings | per-request random nonce; host takes the last `MBRESULT:<nonce>:` line; output forgery gains nothing because comparison happens on the host; performance verdicts use only the host wall clock | `ForgedFrameIgnored`, `PerformanceUsesHostClock` |
| T7 | Hidden data leaked through prompts | request builders take only `TaskPrompt`; marker test across all families | `HiddenIsolationTests.PromptsHaveNoHiddenMarkers` |
| T8 | Prompt injection in task content making the model leak or act | measured (adversarial-robustness family, `canary` grader); simulated tools have no real effects | family tests |
| T9 | Stored XSS through candidate output in the dashboard | Blazor's default HTML encoding; `MarkupString` is never used with candidate or task content (enforced by a source-scan test) | `UiSafetyTests.NoMarkupStringOnUntrusted` |
| T10 | Terminal escape injection in the CLI | CLI output of untrusted text goes through `ConsoleSafe.Sanitize`, which strips C0/C1 control characters except newline and tab | `ConsoleSafeTests` |
| T11 | API keys persisted, logged or exported | `ResolvedModel.ApiKey` excluded from snapshots; header values masked; raw requests hold bodies only; keys passed via headers | `SecretHandlingTests` |
| T12 | SQL injection via candidate content | EF Core parameterised queries only; no raw SQL with interpolation | code review + grep test |
| T13 | Grader tampering by the candidate | graders run in the host process, never in the container; the container gets no grader code | design |
| T14 | Malicious static/repo benchmark definitions | definitions are treated as trusted user input (the user authored them); repo contents are only ever sent to the model as text and never executed on the host | documented |
| T15 | Tool-call argument abuse (huge or malformed JSON) | simulated environments validate args and return error payloads; argument size is capped at 64 KB | environment tests |

## Residual risks
- Kernel-level container escapes are out of scope. Docker Desktop's VM isolation on Windows adds a layer of protection.
- Performance assertions depend on host load (ADR 0006).
- Remote providers see benchmark prompts. Publicly exposing a suite risks contamination, so prefer fresh seeds.
