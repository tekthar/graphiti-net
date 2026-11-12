# Copyright 2026, graphiti-net contributors
# Licensed under the Apache License, Version 2.0. See LICENSE.
"""Run a parity dataset through Python Graphiti (the pinned release) and dump what it produced as JSON.

Usage: python run_upstream.py <dataset.json> <output.json> [--record <recordings.json>] [--bulk]

With --bulk, the whole dataset is ingested with one add_episode_bulk call instead of one add_episode call per
episode.

With --record, every LLM prompt and answer and every embedding is saved, so the .NET port can replay upstream's
exact model answers on every commit at no token cost (see parity/README.md). Each recorded prompt is keyed by
recording_key() below; the .NET replay client computes the same key.

Reads the same settings as the .NET side (see docs/development.md): NEO4J_URI, NEO4J_USER, NEO4J_PASSWORD,
LLM_MODEL, SMALL_LLM_MODEL, EMBEDDING_MODEL, EMBEDDING_DIM, OPENAI_API_KEY.
"""

import argparse
import asyncio
import base64
import copy
import hashlib
import json
import struct
import logging
import os
from datetime import datetime

from graphiti_core import Graphiti
from graphiti_core.cross_encoder.openai_reranker_client import OpenAIRerankerClient
from graphiti_core.embedder.openai import OpenAIEmbedder, OpenAIEmbedderConfig
from graphiti_core.llm_client.config import LLMConfig
from graphiti_core.llm_client.openai_client import OpenAIClient
from graphiti_core.nodes import EpisodeType
from graphiti_core.search.search_config_recipes import COMBINED_HYBRID_SEARCH_RRF
from graphiti_core.utils.bulk_utils import RawEpisode


def recording_key(messages) -> str:
    """SHA-256 of the prompt messages as built by the prompt library, before the LLM client appends anything."""
    joined = '\u241e'.join(f'{m.role}\u241f{m.content}' for m in messages)
    return hashlib.sha256(joined.encode('utf-8')).hexdigest()


def pack_vector(vector) -> str:
    return base64.b64encode(struct.pack(f'<{len(vector)}f', *vector)).decode('ascii')


class Recorder:
    def __init__(self):
        self.llm: dict[str, dict] = {}
        self.embeddings: dict[str, str] = {}

    def wrap_llm(self, client):
        original = client.generate_response

        async def generate_response(messages, *args, **kwargs):
            pristine = copy.deepcopy(messages)
            response = await original(messages, *args, **kwargs)
            response_model = kwargs.get('response_model', args[0] if args else None)
            self.llm.setdefault(
                recording_key(pristine),
                {
                    'prompt_name': kwargs.get('prompt_name'),
                    'response_model': response_model.__name__ if response_model else None,
                    'messages': [{'role': m.role, 'content': m.content} for m in pristine],
                    'response': response,
                },
            )
            return response

        client.generate_response = generate_response

    def wrap_embedder(self, embedder):
        create, create_batch = embedder.create, embedder.create_batch

        async def record_create(input_data):
            vector = await create(input_data)
            # Search embeds the query as a one-element list.
            text = input_data[0] if isinstance(input_data, list) and len(input_data) == 1 else input_data
            if isinstance(text, str):
                self.embeddings.setdefault(text, pack_vector(vector))
            return vector

        async def record_create_batch(input_data_list):
            vectors = await create_batch(input_data_list)
            for text, vector in zip(input_data_list, vectors, strict=True):
                self.embeddings.setdefault(text, pack_vector(vector))
            return vectors

        embedder.create, embedder.create_batch = record_create, record_create_batch

    def save(self, path: str, settings: dict) -> None:
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(
                {'settings': settings, 'llm': self.llm, 'embeddings': self.embeddings},
                f,
                indent=1,
                ensure_ascii=False,
                sort_keys=True,
            )


def iso(value):
    return value.isoformat() if value is not None else None


async def main(dataset_path: str, output_path: str, record_path: str | None, bulk: bool = False) -> None:
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
    llm_client = OpenAIClient(llm_config)
    recorder = Recorder() if record_path else None
    if recorder:
        recorder.wrap_llm(llm_client)
        recorder.wrap_embedder(embedder)
    graphiti = Graphiti(
        os.environ.get('NEO4J_URI', 'bolt://localhost:7687'),
        os.environ.get('NEO4J_USER', 'neo4j'),
        os.environ['NEO4J_PASSWORD'],
        llm_client=llm_client,
        embedder=embedder,
        cross_encoder=OpenAIRerankerClient(llm_config),
    )
    group_id = dataset['group_id']
    try:
        await graphiti.build_indices_and_constraints()
        await graphiti.driver.execute_query('MATCH (n {group_id: $g}) DETACH DELETE n', g=group_id)

        if bulk:
            await graphiti.add_episode_bulk(
                [
                    RawEpisode(
                        name=episode['name'],
                        content=episode['content'],
                        source_description=episode['source_description'],
                        source=EpisodeType.from_str(episode['source']),
                        reference_time=datetime.fromisoformat(episode['reference_time']),
                    )
                    for episode in dataset['episodes']
                ],
                group_id=group_id,
            )
        else:
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
            'mode': 'bulk' if bulk else 'sequential',
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
        if recorder and record_path:
            recorder.save(
                record_path,
                {
                    'graphiti_core': '0.31.2',
                    'llm_model': llm_config.model,
                    'small_llm_model': llm_config.small_model,
                    'embedding_model': embedder.config.embedding_model,
                    'embedding_dim': embedder.config.embedding_dim,
                },
            )
            print(f'{len(recorder.llm)} prompts, {len(recorder.embeddings)} embeddings -> {record_path}')
    finally:
        await graphiti.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('dataset')
    parser.add_argument('output')
    parser.add_argument('--record', help='save prompts, answers and embeddings to this file')
    parser.add_argument('--bulk', action='store_true', help='ingest with one add_episode_bulk call')
    arguments = parser.parse_args()
    asyncio.run(main(arguments.dataset, arguments.output, arguments.record, arguments.bulk))
