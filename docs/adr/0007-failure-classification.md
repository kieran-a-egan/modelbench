# ADR 0007: Infrastructure vs model failures

**Status:** Accepted

## Decision
- `InfraException` (transport error, HTTP 408/429/5xx, sandbox unavailable) has a `Retryable` flag.
- The runner retries only retryable infra failures, with exponential backoff (`RetryPolicy`). Retry counts are recorded on the attempt.
- Outcomes:
  - `Passed` / `Failed`: graded model results.
  - `InfraError`: infrastructure failed after retries, or a non-retryable infra error.
  - `Timeout`: the attempt exceeded the run timeout. It is excluded from correctness, reported separately, and counted in the reliability view.
  - `Cancelled`: the user cancelled. It is resumable.
- A malformed or empty model response is a model failure (`Failed`), never retried.
- Provider refusals and content filters returned with HTTP 200 are model behaviour.

## Consequences
Leaderboards report infra error counts beside scores, so a flaky endpoint is visible instead of silently lowering scores.
