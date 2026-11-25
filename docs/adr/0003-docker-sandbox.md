# ADR 0003: Docker CLI sandbox with locked-down containers

**Status:** Accepted

## Context
Candidate-generated code is untrusted. It must run with no network, no secrets, limited filesystem access, resource limits and enforced timeouts.

## Decision
- Drive the `docker` CLI through `Process`, which avoids a Docker SDK dependency.
- Use one container per sandbox request, started with:
  `--rm --network none --read-only --tmpfs /tmp:rw,size=64m --memory 256m --memory-swap 256m --cpus 1 --pids-limit 64 --cap-drop ALL --security-opt no-new-privileges --user 65534:65534 -e HOME=/tmp -i`.
- Pass no host environment variables and mount nothing. The request is piped to stdin and read fully by the runner before candidate code loads.
- The runner inside the container does all compiling, executing and AST parsing:
  - Python: `ast` + `importlib`.
  - C#: Roslyn in-memory compilation + reflection invoke.
- Results come back on stdout as `MBRESULT:<nonce>:<base64 json>`. The nonce is random per request, and the host takes the last matching line.
- The host enforces `TimeoutMs + 5s` by running `docker kill <name>`, and caps stdout/stderr capture at 1 MB.
- Docker unavailable or image missing raises `InfraException`. Candidate crash, timeout or OOM is a model failure.

## Consequences
- Each request pays container start-up cost (about 0.5–1.5 s).
- Docker must be running for code families. When it isn't, those attempts become InfraError rather than false failures.
- The runner sources are not secret, but output forgery is pointless because expected values never enter the container.
- Candidate code shares a process with the runner, so in-container timings are forgeable. Performance verdicts therefore use only the host-enforced wall-clock budget (ADR 0006).
