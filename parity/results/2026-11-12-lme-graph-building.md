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

## Larger run: 5 conversations × 20 messages (100 messages)

Same method and models, commit `c1ed7b3` (with the differential-test fixes).

| Candidate | Score | Facts | Entities | Time | Input / output tokens |
| --- | --- | --- | --- | --- | --- |
| Python Graphiti, second run (control) | 0.450 | 438 | 537 | 288 s | 1,547,273 / 75,362 |
| **graphiti-net** | **0.390** | 427 | 519 | 252 s | 1,534,094 / 72,551 |

The baseline run produced 417 facts and 524 entities in 268 s, with 1,528,826 input and 72,051 output tokens.

Per conversation, graphiti-net's fact counts (61, 95, 97, 96, 78) sit between or beside the two upstream runs' (63/70,
97/102, 89/113, 105/99, 63/54). The score gap to the control, 0.06, is 6 messages out of 100, within the noise: each
score's standard error is about 0.05. graphiti-net was the fastest of the three runs. The leaner extraction noticed
in the first run did not recur.

## Caveats

The first run's 36 messages move each score by about 0.03 per message; the larger run's 100 by 0.01. Repeat on
PostgreSQL as well as in memory before a release.
