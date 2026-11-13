# Copyright 2026, graphiti-net contributors
# Licensed under the Apache License, Version 2.0. See LICENSE.
"""Graphiti's own graph-building evaluation, with Python Graphiti as the baseline and graphiti-net as the candidate.

Follows upstream's tests/evals/eval_e2e_graph_building.py (Graphiti v0.31.2):

  build  <longmemeval_oracle.json> <baseline.json> <users> <messages>
      Builds the baseline graphs with Python Graphiti on Neo4j, as upstream's build_baseline_graph.
  judge  <baseline.json> <candidate.json> <report.json>
      For each message, asks an LLM judge with upstream's eval_add_episode_results prompt whether the candidate's
      AddEpisodeResults is worse than the baseline's. The score is the share of messages where it is not, averaged
      per user, as upstream's eval_graph.

The candidate file comes from tools/GraphitiNet.Eval, in the same JSON shape. One deliberate fix: upstream passes
`episodes[0]` and `episodes[1:]` of a single message string as the message and previous messages (its first
character and the rest); this runner passes the message and the messages before it in the same conversation.

Settings as run_upstream.py: NEO4J_URI, NEO4J_USER, NEO4J_PASSWORD, LLM_MODEL, SMALL_LLM_MODEL, EMBEDDING_MODEL,
EMBEDDING_DIM, JUDGE_MODEL (defaults to LLM_MODEL), OPENAI_API_KEY.
"""

import asyncio
import json
import logging
import os
import sys
from datetime import datetime, timezone

from graphiti_core import Graphiti
from graphiti_core.embedder.openai import OpenAIEmbedder, OpenAIEmbedderConfig
from graphiti_core.graphiti import AddEpisodeResults
from graphiti_core.helpers import semaphore_gather
from graphiti_core.llm_client import LLMConfig, OpenAIClient
from graphiti_core.nodes import EpisodeType
from graphiti_core.prompts.eval import EvalAddEpisodeResults, eval_add_episode_results


def llm_config(model: str | None = None) -> LLMConfig:
    default = os.environ.get('LLM_MODEL', 'gpt-5-mini')
    return LLMConfig(model=model or default, small_model=os.environ.get('SMALL_LLM_MODEL', model or default))


async def build(dataset_path: str, out_path: str, users: int, session_length: int) -> None:
    logging.getLogger('neo4j.notifications').setLevel(logging.ERROR)
    with open(dataset_path, encoding='utf-8') as f:
        data = json.load(f)
    graphiti = Graphiti(
        os.environ.get('NEO4J_URI', 'bolt://localhost:7687'),
        os.environ.get('NEO4J_USER', 'neo4j'),
        os.environ['NEO4J_PASSWORD'],
        llm_client=OpenAIClient(llm_config()),
        embedder=OpenAIEmbedder(
            OpenAIEmbedderConfig(
                embedding_model=os.environ.get('EMBEDDING_MODEL', 'text-embedding-3-small'),
                embedding_dim=int(os.environ.get('EMBEDDING_DIM', '1024')),
            )
        ),
    )
    await graphiti.build_indices_and_constraints()

    async def subgraph(index: int):
        user_id = f'lme_oracle_experiment_user_{index}'
        group_id = user_id + '_baseline'
        await graphiti.driver.execute_query('MATCH (n {group_id: $g}) DETACH DELETE n', g=group_id)
        item = data[index]
        results = []
        count = 0
        for session, date in zip(item['haystack_sessions'], item['haystack_dates'], strict=False):
            reference_time = datetime.strptime(date + ' UTC', '%Y/%m/%d (%a) %H:%M UTC').replace(tzinfo=timezone.utc)
            for msg in session:
                if count >= session_length:
                    continue
                count += 1
                r = await graphiti.add_episode(
                    name='',
                    episode_body=f'{msg["role"]}: {msg["content"]}',
                    reference_time=reference_time,
                    source=EpisodeType.message,
                    source_description='',
                    group_id=group_id,
                )
                for node in r.nodes:
                    node.name_embedding = None
                for edge in r.edges:
                    edge.fact_embedding = None
                dumped = r.model_dump(mode='json')
                dumped['_message'] = msg['content']
                results.append(dumped)
        return user_id, results

    started = datetime.now()
    try:
        subgraphs = await semaphore_gather(*[subgraph(i) for i in range(users)])
    finally:
        await graphiti.close()
    with open(out_path, 'w', encoding='utf-8') as f:
        json.dump(dict(subgraphs), f, indent=2, default=str)
    usage = graphiti.token_tracker.get_total_usage()
    print(
        f'{users} users x {session_length} messages in {(datetime.now() - started).seconds}s, '
        f'{usage.input_tokens} input + {usage.output_tokens} output tokens -> {out_path}'
    )


def load(path: str) -> dict[str, list[tuple[AddEpisodeResults, str]]]:
    with open(path, encoding='utf-8') as f:
        raw = json.load(f)
    return {
        user: [(AddEpisodeResults(**{k: v for k, v in item.items() if k != '_message'}), item['_message']) for item in items]
        for user, items in raw.items()
    }


async def judge(baseline_path: str, candidate_path: str, report_path: str) -> None:
    baseline, candidate = load(baseline_path), load(candidate_path)
    llm = OpenAIClient(llm_config(os.environ.get('JUDGE_MODEL')))
    report = {'users': {}, 'decisions': []}
    total = 0.0
    for user in candidate:
        pairs = list(zip(baseline[user], candidate[user], strict=False))

        async def decide(i: int):
            (b, message), (c, _) = pairs[i]
            context = {
                'baseline': b,
                'candidate': c,
                'message': message,
                'previous_messages': [m for (_, m) in baseline[user][:i]],
            }
            response = await llm.generate_response(eval_add_episode_results(context), response_model=EvalAddEpisodeResults)
            return i, response

        decisions = await semaphore_gather(*[decide(i) for i in range(len(pairs))], max_coroutines=8)
        not_worse = 0
        for i, response in sorted(decisions, key=lambda d: d[0]):
            worse = bool(response.get('candidate_is_worse', False))
            not_worse += 0 if worse else 1
            report['decisions'].append(
                {'user': user, 'message': i, 'candidate_is_worse': worse, 'reasoning': response.get('reasoning')}
            )
        user_score = not_worse / len(pairs)
        report['users'][user] = user_score
        total += user_score
    report['score'] = total / len(candidate)
    with open(report_path, 'w', encoding='utf-8') as f:
        json.dump(report, f, indent=2, ensure_ascii=False)
    print(f'score {report["score"]:.3f} (share of messages where the candidate is not worse than the baseline)')
    for user, score in report['users'].items():
        print(f'  {user}: {score:.3f}')


if __name__ == '__main__':
    command = sys.argv[1]
    if command == 'build':
        asyncio.run(build(sys.argv[2], sys.argv[3], int(sys.argv[4]), int(sys.argv[5])))
    elif command == 'judge':
        asyncio.run(judge(sys.argv[2], sys.argv[3], sys.argv[4]))
    else:
        raise SystemExit(__doc__)
