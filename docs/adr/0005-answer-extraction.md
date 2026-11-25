# ADR 0005: Deterministic, declared answer extraction

**Status:** Accepted

## Context
Without an LLM judge, answers must be located in free-form model output mechanically.

## Decision
- Every prompt states the required answer format: `<answer>…</answer>`, a fenced code block, JSON, or free text.
- `AnswerExtractor` first removes `<think>`/`<thinking>` blocks, then takes the **last** match.
- If no match is found, the required `answer-format` assertion fails.
- Normalisation is explicit and per-grader (`normalize`, `absTol`/`relTol`, `unorderedArrays`). The extractor never applies it implicitly.

## Consequences
- A model that answers correctly in the wrong format fails. That is measured as an instruction-fidelity failure, not hidden.
- Users can see the extracted answer in assertion "actual" values.
