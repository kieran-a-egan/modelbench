# ADR 0003: Docker CLI sandbox with locked-down containers

**Status:** Accepted

## Context
Candidate-generated code is untrusted. It must run with no network, no secrets, limited filesystem access, resource limits and enforced timeouts.

## Decision
- Drive the `docker` CLI through `Process`, which avoids a Docker SDK dependency.
- Use one container per sandbox request, started with:
  `--rm --network none --read-only --tmpfs /tmp:rw,size=64m --memory 256m --memory-swap 256m --cpus 1 --pids-limit 64 --cap-drop ALL --security-opt no-new-privileges --user 65534:65534 -e HOME=/tmp -i`.
- Pass no host environment variables beyond `HOME=/tmp` (and, for C#, `DOTNET_EnableDiagnostics=0` and `DOTNET_CLI_TELEMETRY_OPTOUT=1`), and mount nothing. The request is piped to stdin and read fully by the runner before candidate code loads.
- The runner inside the container does all compiling, executing and AST parsing:
  - Python: `ast` + `importlib`.
  - C#: Roslyn in-memory compilation + reflection invoke.
- Candidate code never runs in the runner's process (#3). For every case the runner starts a fresh child process (Python: `fork`; C#: `Runner.dll --case`), which runs one input and returns `{output, error, elapsedMs}` on a private pipe. The child's other file descriptors lead nowhere (`/dev/null`). The runner:
  - checks the child's exit status independently of what it wrote, and takes the case index itself;
  - is PID 1 and not dumpable (`prctl(PR_SET_DUMPABLE, 0)`), so a child of the same user cannot open `/proc/1/fd/1` or `/proc/1/mem`;
  - after each case kills and reaps every leftover process and empties `/tmp`, `/dev/shm` and `/dev/mqueue` and removes SysV IPC objects, so no state reaches the next case.
  Compile and inspect run no candidate code and stay in the runner's process.
- Results come back on stdout as `MBRESULT:<nonce>:<base64 json>`, written only by the runner. The nonce is random per request. The host accepts the frame only when there is exactly one matching line and the container exited 0.
- The runner enforces `TimeoutMs` x repetitions per case by killing the case's process tree; that case's error is `case timed out after N ms` and the other cases still run. The host is the backstop: it enforces `TimeoutMs` x repetitions x inputs + 5 s (compile and inspect: `TimeoutMs` + 5 s) by running `docker kill <name>`, and reports `TimedOut` only then. A failed `docker kill` is written to stderr. The runner also exits by itself 4 s after the last case could legitimately finish, so a dead host cannot leave a container running.
- The runner ignores SIGINT and SIGTERM, so a case cannot stop it by signalling PID 1.
- Host stdout capture is capped at 1 MB. The runner budgets the total JSON of all cases to 740 KB (base64 plus the envelope fits the cap); a case that would exceed it gets the error `output budget exceeded`. The constant is duplicated in both runners and `DockerSandbox.OutputCap` is the cap it must fit.
- Docker unavailable or image missing raises `InfraException`. Candidate crash, timeout or OOM is a model failure.

## Consequences
- Each request pays container start-up cost (about 0.5–1.5 s). Per-case processes add nothing measurable for Python (fork) and about 45 ms per case for C# (a new .NET process): a 33-case C# request went from 1.1 s to 2.6 s (verification log, 2026-11-26).
- Docker must be running for code families. When it isn't, those attempts become InfraError rather than false failures.
- The runner sources are not secret, but output forgery is pointless because expected values never enter the container.
- A case can still write its own pipe directly, which is equivalent to returning a value, so in-container timings are forgeable. Performance verdicts therefore use only the host-enforced wall-clock budget (ADR 0006).
- A hung case fails only itself, at its per-case deadline. `PerformanceGrader` treats a case killed at the budget like a host kill ("killed at budget").
- The image tags are `:3`; the tag changes whenever the runner protocol does.
