# ModelBench threat model

## Scope and trust boundaries

| Zone | Trust | Contents |
|---|---|---|
| Host process (Cli/Web/Core) | trusted | generators, reference solvers, graders, hidden data, API keys (in memory only) |
| SQLite DB | trusted storage, but holds **untrusted content** | candidate responses, transcripts |
| Local model endpoints | semi-trusted transport, **untrusted output** | candidate text, tool calls |
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
| T6 | Forged result frames or timings | candidate code runs only in a child process per case whose fds lead to its own result pipe or `/dev/null`; the runner (PID 1, not dumpable) is the only frame writer; the host requires exactly one `MBRESULT:<nonce>:` line, exit code 0 and case indices `0..n-1`, else the result is unusable; expected values never enter the container and comparison happens on the host; performance verdicts use only the host wall clock and are unscored | `ForgedFrameIgnored`, `RunnerIntegrityTests`, `MalformedCaseIndicesAreUnusableNeverAPass` |
| T16 | State carried between test cases | a fresh process per case (fresh module and statics, environment, builtins); after each case the runner kills and reaps leftover processes, empties `/tmp`, `/dev/shm` and `/dev/mqueue`, and removes SysV shared memory, message queues and semaphores | `RunnerIntegrityTests.*StateNeverCarriesToTheNextCase` |
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
- Performance assertions depend on host load (ADR 0006). They are reported but unscored, so this noise cannot move a score or a ranking.
- **Case processes run as the same user as the runner (MB-004, #3).** The container has no capabilities to drop to a second user. What separates them is that the runner is not dumpable and the case holds no descriptor to the frame stream (`RunnerIntegrityTests`). A Python case still has a copy of the nonce, because `fork` copies memory, but it has nowhere to write it. Whatever a case writes on its own result pipe counts as its own return value, which it could have returned anyway. A hung case blocks the request until the host's wall-clock kill, which fails it; it cannot affect another case's result.
- Only local model servers are supported, so prompts do not leave the machine (or network) running the server. This is enforced, not assumed: the catalog refuses an `openai-completions` endpoint unless its host is loopback or a private-network address (`LocalEndpoint.Check`), and the provider's HTTP client re-checks the resolved address on every connection (`LocalEndpoint.ConnectAsync`), with proxies and redirects disabled, so DNS changes and redirects cannot forward prompts to a hosted service. Publishing a suite still risks contamination of future models, so prefer fresh seeds.
