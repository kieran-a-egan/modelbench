# ADR 0006: Assertion-based scoring

**Status:** Accepted

## Decision
- Every score comes from `AssertionResult`s. There are no other inputs.
- Attempt score = Σ(weight × score) / Σ(weight). An attempt passes when every required assertion passes.
- A grader that cannot run (no answer) still records its assertions as failed, so the failure view is complete.
- Run pass rate and mean score exclude InfraError, Timeout and Cancelled attempts. Those are reported separately.
- Performance assertions run a large input under a host-enforced wall-clock budget, including container start-up. The budget is at least 5× the time an asymptotically correct solution needs. Input sizes are chosen so the wrong complexity class misses the budget by at least 10×. In-container timings are recorded for information only.

## Consequences
- Performance results can, in principle, flip on an overloaded host. This noise ceiling is documented.
- Re-grading stored responses is otherwise bit-for-bit deterministic. This is covered by a test.
