# ADR 0006: Assertion-based scoring

**Status:** Accepted

## Decision
- Every score comes from `AssertionResult`s. There are no other inputs.
- Attempt score = Σ(weight × score) / Σ(weight). An attempt passes when every required assertion passes.
- A grader that cannot run (no answer) still records its assertions as failed, so the failure view is complete.
- Run pass rate and mean score exclude InfraError, Timeout and Cancelled attempts. Those are reported separately.
- Performance assertions use generous absolute budgets per complexity tier (≥ 5× the expected time of an asymptotically correct solution) and take the minimum of 3 repetitions. The input sizes are chosen so the wrong complexity class misses the budget by ≥ 10×.

## Consequences
- Performance results can, in principle, flip on an overloaded host. This noise ceiling is documented.
- Re-grading stored responses is otherwise bit-for-bit deterministic. This is covered by a test.
