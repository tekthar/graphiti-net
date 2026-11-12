# Copyright 2026, graphiti-net contributors
# Licensed under the Apache License, Version 2.0. See LICENSE.
"""Run a parity dataset through Python Graphiti (the pinned release) and dump what it produced as JSON.

Usage: python run_upstream.py <dataset.json> <output.json>

Reads the same settings as the .NET side (see docs/development.md): NEO4J_URI, NEO4J_USER, NEO4J_PASSWORD,
LLM_MODEL, SMALL_LLM_MODEL, EMBEDDING_MODEL, EMBEDDING_DIM, OPENAI_API_KEY.
"""

import asyncio
import json
import logging
import os
import sys
from datetime import datetime

from graphiti_core import Graphiti
from graphiti_core.cross_encoder.openai_reranker_client import OpenAIRerankerClient
from graphiti_core.embedder.openai import OpenAIEmbedder, OpenAIEmbedderConfig
from graphiti_core.llm_client.config import LLMConfig
from graphiti_core.llm_client.openai_client import OpenAIClient
from graphiti_core.nodes import EpisodeType
from graphiti_core.search.search_config_recipes import COMBINED_HYBRID_SEARCH_RRF


def iso(value):
    return value.isoformat() if value is not None else None


async def main(dataset_path: str, output_path: str) -> None:
    # Neo4j reports unknown property keys on a fresh database as warnings; they are noise here.
    logging.getLogger('neo4j.notifications').setLevel(logging.ERROR)

    with open(dataset_path, encoding='utf-8') as f:
        dataset = json.load(f)

    llm_config = LLMConfig(
        model=os.environ.get('LLM_MODEL', 'gpt-5-mini'),
        small_model=os.environ.get('SMALL_LLM_MODEL', os.environ.get('LLM_MODEL', 'gpt-5-mini')),
    )
    embedder = OpenAIEmbedder(
        OpenAIEmbedderConfig(
            embedding_model=os.environ.get('EMBEDDING_MODEL', 'text-embedding-3-small'),
            embedding_dim=int(os.environ.get('EMBEDDING_DIM', '1024')),
        )
    )
    graphiti = Graphiti(
        os.environ.get('NEO4J_URI', 'bolt://localhost:7687'),
        os.environ.get('NEO4J_USER', 'neo4j'),
        os.environ['NEO4J_PASSWORD'],
        llm_client=OpenAIClient(llm_config),
        embedder=embedder,
        cross_encoder=OpenAIRerankerClient(llm_config),
    )
    group_id = dataset['group_id']
    try:
        await graphiti.build_indices_and_constraints()
        await graphiti.driver.execute_query('MATCH (n {group_id: $g}) DETACH DELETE n', g=group_id)

        for episode in dataset['episodes']:
            await graphiti.add_episode(
                name=episode['name'],
                episode_body=episode['content'],
                source_description=episode['source_description'],
                reference_time=datetime.fromisoformat(episode['reference_time']),
                source=EpisodeType.from_str(episode['source']),
                group_id=group_id,
            )

        nodes, _, _ = await graphiti.driver.execute_query(
            'MATCH (n:Entity {group_id: $g}) RETURN n.name AS name, labels(n) AS labels, n.summary AS summary',
            g=group_id,
        )
        edges, _, _ = await graphiti.driver.execute_query(
            """
            MATCH (s:Entity {group_id: $g})-[e:RELATES_TO]->(t:Entity)
            RETURN s.name AS source, e.name AS name, t.name AS target, e.fact AS fact,
                   e.valid_at AS valid_at, e.invalid_at AS invalid_at, e.expired_at AS expired_at
            """,
            g=group_id,
        )

        searches = []
        for query in dataset['queries']:
            facts = await graphiti.search(query, group_ids=[group_id], num_results=5)
            combined = await graphiti.search_(query, config=COMBINED_HYBRID_SEARCH_RRF, group_ids=[group_id])
            searches.append(
                {
                    'query': query,
                    'facts': [e.fact for e in facts],
                    'combined_nodes': [n.name for n in combined.nodes],
                    'combined_edges': [e.fact for e in combined.edges],
                }
            )

        result = {
            'implementation': 'graphiti-core',
            'dataset': dataset['name'],
            'entities': sorted(
                ({'name': r['name'], 'labels': sorted(r['labels']), 'summary': r['summary']} for r in nodes),
                key=lambda n: n['name'],
            ),
            'facts': sorted(
                (
                    {
                        'source': r['source'],
                        'name': r['name'],
                        'target': r['target'],
                        'fact': r['fact'],
                        'valid_at': iso(r['valid_at'] and r['valid_at'].to_native()),
                        'invalid_at': iso(r['invalid_at'] and r['invalid_at'].to_native()),
                        'expired': r['expired_at'] is not None,
                    }
                    for r in edges
                ),
                key=lambda e: (e['source'], e['name'], e['target'], e['fact']),
            ),
            'searches': searches,
        }
        with open(output_path, 'w', encoding='utf-8') as f:
            json.dump(result, f, indent=2, ensure_ascii=False)
        print(f'{len(result["entities"])} entities, {len(result["facts"])} facts -> {output_path}')
    finally:
        await graphiti.close()


if __name__ == '__main__':
    asyncio.run(main(sys.argv[1], sys.argv[2]))
