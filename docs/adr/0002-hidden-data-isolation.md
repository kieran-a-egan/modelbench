# ADR 0002: Hidden data never reaches the candidate or the sandbox

**Status:** Accepted

## Context
Hidden tests, expected answers and reference solutions must never be exposed to candidate models or candidate code. Candidate code that can read expected outputs at runtime could return them directly.

## Decision
- Each `TaskInstance` stores the `TaskPrompt` (visible) and the `TaskHidden` (grading) in separate columns. Request builders accept only `TaskPrompt`.
- `SandboxRequest` carries only candidate code, an entry point and argument lists. Expected outputs are compared on the host.
- Differential testing uses a reference solver written in C#, which runs on the host at generation time. Its outputs are stored in `TaskHidden`.
- Simulated tools run in-process on the host. The model sees only tool results.
- Test: a marker string placed in hidden data must not appear in any serialized provider request or any sandbox `request.json`, for every family.

## Consequences
- Code tasks need entry-point style specs (function name and signature) instead of free-form programs.
- I/O-style programs are not supported.
