# ADR 0006: Assertion-based scoring

**Status:** Accepted

## Decision
- Every score comes from `AssertionResult`s. There are no other inputs.
- Attempt score = Σ(weight × score) / Σ(weight). An attempt passes when every required assertion passes.
- A grader that cannot run (no answer) still records its assertions as failed, so the failure view is complete.
- Run graded pass rate and mean score exclude InfraError, Timeout and Cancelled attempts. Those are reported separately.
- (Revised 2026-11-26, issue #1.) The leaderboard ranks by the end-to-end rate, passes / (graded + timeouts), because a timeout is the model exhausting its own budget. Completion, (graded + timeouts) / attempts, discloses infra errors and cancellations.
- (Revised 2026-11-26.) Performance credit requires a correct output. Performance graders carry weight 0 and are not required, so host timing is reported but never moves a score or pass/fail.
- (Revised 2026-11-26.) The grading methodology is versioned (`GradingPipeline.Version`, stored per run). Runs of different versions are never ranked or compared together.
- Performance assertions run a large input under a host-enforced wall-clock budget, including container start-up. The budget is at least 5× the time an asymptotically correct solution needs. Input sizes are chosen so the wrong complexity class misses the budget by at least 10×. In-container timings are recorded for information only.

## Consequences
- Performance results can flip on an overloaded host, so they are informational only, and re-grading skips `within-budget`.
- Re-grading stored responses is otherwise bit-for-bit deterministic. This is covered by a test, including one that regrades on a host slow enough to miss every budget.
- A correct but inefficient solution now passes a code task; its inefficiency shows only in the reported `within-budget` assertion.
