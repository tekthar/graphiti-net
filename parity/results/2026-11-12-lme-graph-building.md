# Graph building on LongMemEval: graphiti-net against Python Graphiti

Date: 2026-11-12. Upstream: Graphiti v0.31.2 on Neo4j 5.26. graphiti-net: in-memory driver, commit `c0dd45d`.

## Method

Graphiti's own evaluation (`tests/evals/eval_e2e_graph_building.py`): build a graph message by message from
LongMemEval conversations (`longmemeval_oracle.json`, shipped with upstream), once with a baseline and once with a
candidate, then ask an LLM judge with upstream's `eval_add_episode_results` prompt, message by message, whether the
candidate's extraction is worse than the baseline's. The score is the share of messages where it is not, averaged
per conversation. Run with `parity/python/lme_eval.py` and `tools/GraphitiNet.Eval`.

- Data: the first 3 conversations, the first 12 messages of each (36 messages).
- Models for building: `gpt-5-mini` for both model sizes, `text-embedding-3-small` at 1,024 dimensions, for both
  implementations.
- Baseline: Python Graphiti. Judge: `gpt-5.5`.

## Results

| Candidate | Score | Facts | Entities | Time | Input / output tokens |
| --- | --- | --- | --- | --- | --- |
| Python Graphiti, second run (control) | 0.361 | 142 | — | 180 s | 490,981 / 24,120 |
| **graphiti-net** | **0.472** | 140 | 172 | 151 s | 503,351 / 23,488 |
| graphiti-net with every fact removed (sanity check) | 0.111 | 0 | 172 | — | — |

The baseline run itself produced 152 facts and 169 entities in 168 s, with 507,524 input and 25,158 output tokens.

**Reading it.** The judge has to pick a side unless two extractions are nearly identical, and two runs of the same
LLM pipeline never are, so even Python Graphiti against itself scores well below 1. graphiti-net scores at least as
well against upstream as upstream does against itself, which puts it within upstream's own run-to-run variation.
Removing every fact drops the score to 0.111, so the judge does see real quality differences.

**The judge matters.** With `gpt-5-mini` as the judge, graphiti-net scored 1.000 and the candidate with every fact
removed still scored 0.944: that judge barely discriminates. Use a strong judge model, and always run the control
and the sanity check alongside.

**To watch.** On one conversation graphiti-net extracted 47 facts against upstream's 68 and 65. The judge didn't
rate that conversation lower than the others, but a larger run should check whether extraction is systematically
leaner.

## Caveats

36 messages is a small sample, and each score moves by about 0.03 per message. Repeat with more conversations before
a release, and on PostgreSQL as well as in memory.
