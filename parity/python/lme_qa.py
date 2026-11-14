# Copyright 2026, graphiti-net contributors
# Licensed under the Apache License, Version 2.0. See LICENSE.
"""Question answering on LongMemEval, the same way for Python Graphiti and graphiti-net.

For each selected question of longmemeval_oracle.json: ingest its haystack sessions message by message (as upstream's
tests/evals/eval_e2e_graph_building.py: episode body "role: content", reference time from haystack_dates, source
message, one group per question), search the question's group, answer with upstream's eval.qa_prompt from the search
results only, and grade the answer against the gold answer with upstream's eval.eval_prompt (an LLM judge).

  select <longmemeval_oracle.json> <questions.json> [per_type] [seed] [pool]
      Picks per_type questions (default 4) of each question type, at random with a fixed seed (default 42), among
      the pool (default 12) with the fewest haystack messages. Abstention questions (ids ending in _abs) are left out.
  run    <longmemeval_oracle.json> <questions.json> <answers.json> [label]
      Python Graphiti v0.31.2 on Neo4j: ingest, search, answer. tools/GraphitiNet.Eval `qa` writes the same file
      shape for graphiti-net.
  answer <answers.json> <out.json>
      Answers again from the contexts already in <answers.json>, without ingesting or searching (a control for the
      answering stage alone).
  grade  <answers.json> <graded.json>
      Grades every answer with eval.eval_prompt. Also checks that the qa_prompt the answering side sent is the one
      upstream's qa_prompt builds from the stored context, character for character.
  report <graded.json>...
      Prints accuracy overall and per question type, ingestion time and tokens, and search latency.

Prompts come from the prompt library (prompt_library.eval.*), as Graphiti's own calls do, so the system message
ends with upstream's DO_NOT_ESCAPE_UNICODE note on both sides.

Retrieval: Graphiti.search_ with COMBINED_HYBRID_SEARCH_RRF, limited to facts and entities, 20 of each (the hybrid
recipe; episodes and communities are left out because qa_prompt takes only entity summaries and facts). The context
is built as upstream's search_results_to_context_string builds it: facts as {fact, valid_at, invalid_at} with
str(datetime) dates and "Present" for no invalid_at, entities as {entity_name, summary}. The question goes to
qa_prompt with the date it was asked, since LongMemEval's temporal questions count from that date.

Settings as run_upstream.py: NEO4J_URI, NEO4J_USER, NEO4J_PASSWORD, LLM_MODEL, SMALL_LLM_MODEL, EMBEDDING_MODEL,
EMBEDDING_DIM, JUDGE_MODEL (defaults to gpt-5.5), QA_CONCURRENCY (default 8), OPENAI_API_KEY.
"""

import asyncio
import json
import logging
import os
import random
import statistics
import sys
import time
from collections import defaultdict
from datetime import datetime, timezone

from graphiti_core import Graphiti
from graphiti_core.cross_encoder.openai_reranker_client import OpenAIRerankerClient
from graphiti_core.driver.neo4j_driver import Neo4jDriver
from graphiti_core.embedder.openai import OpenAIEmbedder, OpenAIEmbedderConfig
from graphiti_core.llm_client import LLMConfig, LLMClient, OpenAIClient
from graphiti_core.llm_client.openai_generic_client import OpenAIGenericClient
from graphiti_core.nodes import EpisodeType
from graphiti_core.prompts import prompt_library
from graphiti_core.prompts.eval import EvalResponse, QAResponse
from graphiti_core.search.search_config_recipes import COMBINED_HYBRID_SEARCH_RRF

SEARCH_LIMIT = 20
TYPES_ORDER = [
    'single-session-user',
    'single-session-assistant',
    'single-session-preference',
    'multi-session',
    'knowledge-update',
    'temporal-reasoning',
]


def llm_config(model: str | None = None) -> LLMConfig:
    default = os.environ.get('LLM_MODEL', 'gpt-5-mini')
    return LLMConfig(model=model or default, small_model=os.environ.get('SMALL_LLM_MODEL', model or default))


def build_llm() -> LLMClient:
    """The client that builds graphs and answers: OpenAI by default, or any OpenAI-compatible endpoint (DeepSeek,
    vLLM, ...) through upstream's OpenAIGenericClient when LLM_BASE_URL is set. The grader always uses OpenAI."""
    base_url = os.environ.get('LLM_BASE_URL')
    if not base_url:
        return OpenAIClient(llm_config())
    config = llm_config()
    config.base_url = base_url
    config.api_key = os.environ.get('LLM_API_KEY', 'dummy')
    client = OpenAIGenericClient(config, structured_output_mode=os.environ.get('LLM_STRUCTURED_OUTPUT', 'json_object'))
    # Upstream's generic client does not record token usage; record it from each response so runs report tokens.
    create = client.client.chat.completions.create

    async def tracked(*args, **kwargs):
        response = await create(*args, **kwargs)
        if response.usage is not None:
            client.token_tracker.record(None, response.usage.prompt_tokens, response.usage.completion_tokens)
        return response

    client.client.chat.completions.create = tracked
    return client


def qa_query(item: dict) -> str:
    """The question as qa_prompt receives it: the question and the date it was asked."""
    return f'{item["question"]} (asked on {item["question_date"]})'


def qa_context(query: str, facts: list[dict], entities: list[dict]) -> dict:
    return {'query': query, 'entity_summaries': entities, 'facts': facts}


def select(dataset_path: str, out_path: str, per_type: int = 4, seed: int = 42, pool: int = 12) -> None:
    with open(dataset_path, encoding='utf-8') as f:
        data = json.load(f)
    by_type = defaultdict(list)
    for item in data:
        if item['question_id'].endswith('_abs'):
            continue
        messages = sum(len(s) for s in item['haystack_sessions'])
        by_type[item['question_type']].append((messages, item['question_id']))
    rng = random.Random(seed)
    selected = []
    for question_type in sorted(by_type):
        smallest = sorted(by_type[question_type])[:pool]
        count = min(per_type, len(smallest))
        selected += [qid for _, qid in sorted(rng.sample(smallest, count), key=lambda p: p[1])]
    with open(out_path, 'w', encoding='utf-8') as f:
        json.dump(selected, f, indent=2)
    print(f'{len(selected)} questions -> {out_path}')


def load_questions(dataset_path: str, ids_path: str) -> list[dict]:
    with open(dataset_path, encoding='utf-8') as f:
        data = {item['question_id']: item for item in json.load(f)}
    with open(ids_path, encoding='utf-8') as f:
        return [data[qid] for qid in json.load(f)]


MAX_ATTEMPTS = 3


async def run(dataset_path: str, ids_path: str, out_path: str, label: str = 'upstream') -> None:
    logging.getLogger('neo4j.notifications').setLevel(logging.ERROR)
    questions = load_questions(dataset_path, ids_path)
    driver = Neo4jDriver(
        os.environ.get('NEO4J_URI', 'bolt://localhost:7687'),
        os.environ.get('NEO4J_USER', 'neo4j'),
        os.environ['NEO4J_PASSWORD'],
    )
    embedder_config = OpenAIEmbedderConfig(
        embedding_model=os.environ.get('EMBEDDING_MODEL', 'text-embedding-3-small'),
        embedding_dim=int(os.environ.get('EMBEDDING_DIM', '1024')),
    )

    def new_graphiti() -> Graphiti:
        # One client per question, so each question's token usage is its own.
        return Graphiti(
            graph_driver=driver,
            llm_client=build_llm(),
            embedder=OpenAIEmbedder(embedder_config),
            cross_encoder=OpenAIRerankerClient(llm_config()),
        )

    await new_graphiti().build_indices_and_constraints()
    config = COMBINED_HYBRID_SEARCH_RRF.model_copy(deep=True)
    config.episode_config = None
    config.community_config = None
    config.limit = SEARCH_LIMIT
    gate = asyncio.Semaphore(int(os.environ.get('QA_CONCURRENCY', '8')))

    # Each finished question is appended to a checkpoint file, so a run stopped part way (quota, crash) resumes with
    # the questions it has not answered yet.
    checkpoint = out_path + '.partial.jsonl'
    done: dict[str, dict] = {}
    if os.path.exists(checkpoint):
        with open(checkpoint, encoding='utf-8') as f:
            for line in f:
                if line.strip():
                    row = json.loads(line)
                    done[row['question_id']] = row
        print(f'resuming: {len(done)} questions already answered', flush=True)

    async def one(item: dict) -> dict:
        if item['question_id'] in done:
            return done[item['question_id']]
        answered = await one_attempts(item)
        with open(checkpoint, 'a', encoding='utf-8') as f:
            f.write(json.dumps(answered, ensure_ascii=False) + '\n')
        return answered

    async def one_attempts(item: dict) -> dict:
        # A transient provider error (rate limit, 502) fails the whole add_episode, as in Graphiti itself; retry the
        # question from scratch in a fresh group rather than lose the run.
        for attempt in range(MAX_ATTEMPTS):
            try:
                return await attempt_one(item, attempt)
            except Exception as e:  # noqa: BLE001 - any provider error; the last one is re-raised
                if attempt == MAX_ATTEMPTS - 1:
                    raise
                print(
                    f'{item["question_id"]}: attempt {attempt + 1} failed ({type(e).__name__}: {str(e)[:500]}); retrying',
                    flush=True,
                )
                await asyncio.sleep(30)
        raise AssertionError('unreachable')

    async def attempt_one(item: dict, attempt: int) -> dict:
        async with gate:
            graphiti = new_graphiti()
            group_id = f'lme_qa_{label}_{item["question_id"]}' + (f'_r{attempt}' if attempt else '')
            await driver.execute_query('MATCH (n {group_id: $g}) DETACH DELETE n', g=group_id)
            started = time.perf_counter()
            messages = 0
            for session, date in zip(item['haystack_sessions'], item['haystack_dates'], strict=True):
                reference_time = datetime.strptime(date + ' UTC', '%Y/%m/%d (%a) %H:%M UTC').replace(
                    tzinfo=timezone.utc
                )
                for msg in session:
                    messages += 1
                    await graphiti.add_episode(
                        name='',
                        episode_body=f'{msg["role"]}: {msg["content"]}',
                        reference_time=reference_time,
                        source=EpisodeType.message,
                        source_description='',
                        group_id=group_id,
                    )
            ingest_seconds = time.perf_counter() - started
            ingest_usage = graphiti.token_tracker.get_total_usage()

            started = time.perf_counter()
            results = await graphiti.search_(item['question'], config=config, group_ids=[group_id])
            search_ms = (time.perf_counter() - started) * 1000
            facts = [
                {'fact': e.fact, 'valid_at': str(e.valid_at), 'invalid_at': str(e.invalid_at or 'Present')}
                for e in results.edges
            ]
            entities = [{'entity_name': n.name, 'summary': n.summary} for n in results.nodes]
            answered = await answer_one(graphiti.llm_client, qa_query(item), facts, entities)
            print(f'{item["question_id"]}: {messages} messages in {ingest_seconds:.0f}s', flush=True)
            return {
                'question_id': item['question_id'],
                'question_type': item['question_type'],
                'question': item['question'],
                'question_date': item['question_date'],
                'gold_answer': item['answer'],
                'messages': messages,
                'ingest_seconds': round(ingest_seconds, 1),
                'ingest_input_tokens': ingest_usage.input_tokens,
                'ingest_output_tokens': ingest_usage.output_tokens,
                'search_ms': round(search_ms, 1),
                'facts': facts,
                'entity_summaries': entities,
                **answered,
            }

    started = time.perf_counter()
    try:
        answers = await asyncio.gather(*[one(item) for item in questions])
    finally:
        await driver.close()
    write(out_path, {'implementation': 'graphiti 0.31.2 (Neo4j)', 'label': label, 'answers': answers})
    os.remove(checkpoint)
    print(f'{len(answers)} questions in {time.perf_counter() - started:.0f}s -> {out_path}')


async def answer_one(llm: LLMClient, query: str, facts: list[dict], entities: list[dict]) -> dict:
    messages = prompt_library.eval.qa_prompt(qa_context(query, facts, entities))
    prompt_system, prompt_user = messages[0].content, messages[1].content
    before = llm.token_tracker.get_total_usage()
    response = await llm.generate_response(messages, response_model=QAResponse, prompt_name='eval.qa_prompt')
    after = llm.token_tracker.get_total_usage()
    return {
        'qa_query': query,
        'qa_prompt_system': prompt_system,
        'qa_prompt_user': prompt_user,
        'response': response.get('ANSWER', ''),
        'answer_input_tokens': after.input_tokens - before.input_tokens,
        'answer_output_tokens': after.output_tokens - before.output_tokens,
    }


async def answer(in_path: str, out_path: str) -> None:
    run_data = read(in_path)
    gate = asyncio.Semaphore(int(os.environ.get('QA_CONCURRENCY', '8')))

    async def one(a: dict) -> dict:
        async with gate:
            answered = await answer_one(build_llm(), a['qa_query'], a['facts'], a['entity_summaries'])
            return {**a, **answered}

    run_data['answers'] = await asyncio.gather(*[one(a) for a in run_data['answers']])
    run_data['label'] = run_data.get('label', '') + '+reanswer'
    write(out_path, run_data)


async def grade(in_path: str, out_path: str) -> None:
    run_data = read(in_path)
    judge = OpenAIClient(llm_config(os.environ.get('JUDGE_MODEL', 'gpt-5.5')))
    gate = asyncio.Semaphore(8)
    mismatches = 0

    async def one(a: dict) -> dict:
        nonlocal mismatches
        expected = prompt_library.eval.qa_prompt(qa_context(a['qa_query'], a['facts'], a['entity_summaries']))
        if [m.content for m in expected] != [a['qa_prompt_system'], a['qa_prompt_user']]:
            mismatches += 1
        async with gate:
            r = await judge.generate_response(
                prompt_library.eval.eval_prompt({'query': a['question'], 'answer': a['gold_answer'], 'response': a['response']}),
                response_model=EvalResponse,
                prompt_name='eval.eval_prompt',
            )
        return {**a, 'is_correct': bool(r.get('is_correct')), 'grade_reasoning': r.get('reasoning', '')}

    run_data['answers'] = await asyncio.gather(*[one(a) for a in run_data['answers']])
    run_data['judge_model'] = judge.config.model
    run_data['qa_prompt_mismatches'] = mismatches
    write(out_path, run_data)
    if mismatches:
        print(f'WARNING: {mismatches} qa_prompt texts differ from upstream qa_prompt on the stored context')
    summarize(run_data)


def summarize(run_data: dict) -> None:
    answers = run_data['answers']
    correct = sum(a['is_correct'] for a in answers)
    print(f'{run_data.get("implementation")} [{run_data.get("label")}]: {correct}/{len(answers)} correct')
    by_type = defaultdict(list)
    for a in answers:
        by_type[a['question_type']].append(a['is_correct'])
    for t in TYPES_ORDER:
        if t in by_type:
            print(f'  {t}: {sum(by_type[t])}/{len(by_type[t])}')
    print(
        f'  ingest: {sum(a["ingest_seconds"] for a in answers):.0f}s summed, '
        f'{sum(a["ingest_input_tokens"] for a in answers):,} in / {sum(a["ingest_output_tokens"] for a in answers):,} out; '
        f'search median {statistics.median(a["search_ms"] for a in answers):.0f} ms; '
        f'facts {sum(len(a["facts"]) for a in answers)}, entities {sum(len(a["entity_summaries"]) for a in answers)}; '
        f'answer {sum(a["answer_input_tokens"] for a in answers):,} in / {sum(a["answer_output_tokens"] for a in answers):,} out'
    )


def report(paths: list[str]) -> None:
    for path in paths:
        summarize(read(path))


def read(path: str) -> dict:
    with open(path, encoding='utf-8') as f:
        return json.load(f)


def write(path: str, data: dict) -> None:
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(data, f, indent=1, ensure_ascii=False)


if __name__ == '__main__':
    command, rest = sys.argv[1], sys.argv[2:]
    if command == 'select':
        select(rest[0], rest[1], *(int(x) for x in rest[2:]))
    elif command == 'run':
        asyncio.run(run(*rest))
    elif command == 'answer':
        asyncio.run(answer(*rest))
    elif command == 'grade':
        asyncio.run(grade(*rest))
    elif command == 'report':
        report(rest)
    else:
        raise SystemExit(__doc__)
