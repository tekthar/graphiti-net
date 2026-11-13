# Copyright 2026, graphiti-net contributors
# Licensed under the Apache License, Version 2.0. See LICENSE.
"""Writes the differential test fixtures: generated inputs and what upstream's own functions return for them.

Run with Python Graphiti v0.31.2 installed (``pip install graphiti-core==0.31.2``):

    python -I parity/python/generate_differential_fixtures.py

Every input is generated from a fixed seed, so a rerun writes the same files. The C# tests in
tests/GraphitiNet.Core.Tests/Differential load the files, call the port with the same inputs and compare.

Value encoding in the files (plain JSON otherwise; numbers with a '.' or an exponent are floats):
  {"$dt": "<isoformat>"}            a datetime (naive when the text has no offset)
  {"$now": true}                    the frozen utc_now() of the generator: the port must set "now"
  {"$float": "nan"|"inf"|"-inf"}    non-finite floats
  {"$tuple": [...]}                 a tuple
  {"$set": [...]}                   a set (sorted by code point; compare as a set)
  {"$pairs": [[k, v], ...]}         a dict whose keys are not all plain strings
  {"$raises": "<ExceptionType>"}    upstream raised instead of returning

Markers next to a case's input and output, for documented deviations (docs/api-compatibility.md):
  "deviation": "<why>"   the output is what the port must return, derived from upstream's (see the reason)
  "astral": true         text outside the Basic Multilingual Plane, which content chunking measures in UTF-16
  "unordered": true      upstream orders part of the result by Python set iteration; compare as a set
  "order_dependent"      (in _resolve_with_similarity outputs) extracted nodes whose best fuzzy candidates tie
"""

from __future__ import annotations

import json
import logging
import math
import os
import random
import signal
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

# Upstream reads these at import time; the fixtures use upstream's defaults.
for _var in (
    'GRAPHITI_ATTRIBUTE_MAX_LENGTH',
    'CHUNK_TOKEN_SIZE',
    'CHUNK_OVERLAP_TOKENS',
    'CHUNK_MIN_TOKENS',
    'CHUNK_DENSITY_THRESHOLD',
):
    os.environ.pop(_var, None)

import numpy as np  # noqa: E402
from pydantic import BaseModel, Field  # noqa: E402

from graphiti_core.edges import EntityEdge  # noqa: E402
from graphiti_core.helpers import normalize_l2  # noqa: E402
from graphiti_core.nodes import EntityNode, EpisodeType, EpisodicNode  # noqa: E402
from graphiti_core.prompts.prompt_helpers import to_prompt_json  # noqa: E402
from graphiti_core.utils.datetime_utils import ensure_utc  # noqa: E402
from graphiti_core.search.search_utils import (  # noqa: E402
    calculate_cosine_similarity,
    maximal_marginal_relevance,
    rrf,
)
from graphiti_core.utils import bulk_utils, content_chunking, text_utils  # noqa: E402
from graphiti_core.utils.maintenance import (  # noqa: E402
    attribute_utils,
    community_operations,
    dedup_helpers,
    edge_operations,
    temporal_edge_utils,
)

UPSTREAM_VERSION = '0.31.2'
ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'tests' / 'GraphitiNet.Core.Tests' / 'Differential' / 'Fixtures'

UTC = timezone.utc
FROZEN_NOW = datetime(2099, 9, 9, 9, 9, 9, 909090, tzinfo=UTC)

# ---------------------------------------------------------------------------------------------------------------
# Encoding


def enc(value):
    if value is None or isinstance(value, (bool, str)):
        return value
    if isinstance(value, int):
        return value
    if isinstance(value, float):
        if math.isnan(value):
            return {'$float': 'nan'}
        if math.isinf(value):
            return {'$float': 'inf' if value > 0 else '-inf'}
        return value
    if isinstance(value, np.floating):
        return enc(float(value))
    if isinstance(value, np.integer):
        return int(value)
    if isinstance(value, np.ndarray):
        return [enc(v) for v in value.tolist()]
    if isinstance(value, datetime):
        if value == FROZEN_NOW:
            return {'$now': True}
        return {'$dt': value.isoformat()}
    if isinstance(value, tuple):
        return {'$tuple': [enc(v) for v in value]}
    if isinstance(value, (set, frozenset)):
        return {'$set': [enc(v) for v in sorted(value)]}
    if isinstance(value, list):
        return [enc(v) for v in value]
    if isinstance(value, dict):
        if list(value) == ['$raises']:
            return value
        if all(isinstance(k, str) and not k.startswith('$') for k in value):
            return {k: enc(v) for k, v in value.items()}
        return {'$pairs': [[enc(k), enc(v)] for k, v in value.items()]}
    raise TypeError(f'cannot encode {type(value)!r}')


def call(fn, *args, **kwargs):
    try:
        return fn(*args, **kwargs)
    except Exception as exc:  # noqa: BLE001 - the exception type is the expected output
        return {'$raises': type(exc).__name__}


def write(name: str, functions: list[str], cases: dict[str, list]) -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    document = {
        '_notice': (
            'Generated by parity/python/generate_differential_fixtures.py from the output of '
            + ', '.join(functions)
            + f' in Graphiti v{UPSTREAM_VERSION} (graphiti_core, Copyright 2024 Zep Software, Inc., '
            'Apache-2.0). Do not edit by hand.'
        ),
        '_upstream_version': UPSTREAM_VERSION,
        **cases,
    }
    path = OUT / f'{name}.json'
    # One case per line keeps the files small and their diffs readable.
    lines = ['{']
    header = list(document.items())[:2]
    for key, value in header:
        lines.append(f'{json.dumps(key)}: {json.dumps(value, ensure_ascii=False)},')
    for i, (key, items) in enumerate(cases.items()):
        lines.append(f'{json.dumps(key)}: [')
        for j, item in enumerate(items):
            text = json.dumps(item, ensure_ascii=False, separators=(',', ':'))
            lines.append(text + (',' if j < len(items) - 1 else ''))
        lines.append(']' + (',' if i < len(cases) - 1 else ''))
    lines.append('}')
    text = '\n'.join(lines) + '\n'
    assert json.loads(text) == document
    path.write_text(text, encoding='utf-8')
    counts = ', '.join(f'{k}={len(v)}' for k, v in cases.items())
    print(f'{path.relative_to(ROOT)}: {counts}')


# ---------------------------------------------------------------------------------------------------------------
# Text pools

ASCII_WORDS = [
    'alice', 'bob', 'acme', 'corp', 'project', 'apollo', 'data', 'graph', 'search', 'engine', 'paris', 'london',
    'river', 'blue', 'green', 'north', 'south', 'kim', 'lee', 'ana', 'smith', 'johnson', 'o\'brien', 'mc-donald',
    'x', 'ab', 'aaa', 'zzz', '42', '2024', 'v2', 'q3',
]
UNICODE_WORDS = [
    'محمد', 'شركة', 'القاهرة', 'مُحَمَّد', 'İstanbul', 'ΟΔΟΣ', 'Straße', 'ﬁle', 'Kelvin', 'ſtar', 'Ǆemal',
    'café', 'naïve', 'Ĳssel', '東京', '数据', 'Ελληνικά', '😀', '👩‍💻', '🇪🇬', 'a😀b', 'Ａｃｍｅ', '١٢٣',
]
SPACES = [' ', ' ', ' ', '  ', '\t', '\n', '\xa0', '　', '\x1c', '\x1f', '\x85', ' ', ' ', '​']


def rand_word(rng: random.Random, unicode_share: float = 0.3) -> str:
    if rng.random() < unicode_share:
        return rng.choice(UNICODE_WORDS)
    word = rng.choice(ASCII_WORDS)
    style = rng.random()
    if style < 0.2:
        return word.upper()
    if style < 0.5:
        return word.capitalize()
    return word


def rand_name(rng: random.Random, unicode_share: float = 0.3) -> str:
    words = [rand_word(rng, unicode_share) for _ in range(rng.randint(1, 4))]
    out = words[0]
    for w in words[1:]:
        out += rng.choice(SPACES) + w
    if rng.random() < 0.15:
        out = rng.choice(SPACES) + out
    if rng.random() < 0.15:
        out += rng.choice(SPACES)
    if rng.random() < 0.1:
        out += rng.choice(['.', ',', '!', '-', '&', '(Inc)', '’s', "'s"])
    return out


def f32(x: float) -> float:
    return float(np.float32(x))


# ---------------------------------------------------------------------------------------------------------------
# search_utils.py / helpers.py


def gen_search_utils() -> None:
    rng = random.Random(1001)

    def vec(n, scale=1.0):
        return [f32(rng.uniform(-scale, scale)) for _ in range(n)]

    cosine = []
    fixed = [
        ([0.0, 0.0, 0.0], [1.0, 2.0, 3.0]),
        ([1.0, 2.0, 3.0], [0.0, 0.0, 0.0]),
        ([0.0], [0.0]),
        ([1.0, 0.0], [1.0, 0.0]),
        ([1.0, 0.0], [-1.0, 0.0]),
        ([1.0, 0.0], [0.0, 1.0]),
        ([], []),
        ([1.0, 2.0], [1.0, 2.0, 3.0]),
        ([1e-30, 1e-30], [1e-30, -1e-30]),
        ([3e38, 3e38], [3e38, 3e38]),
    ]
    for a, b in fixed:
        a = [f32(x) for x in a]
        b = [f32(x) for x in b]
        out = call(lambda: float(calculate_cosine_similarity(a, b)))  # noqa: B023 - called at once
        cosine.append({'input': {'a': a, 'b': b}, 'output': enc(out)})
    for i in range(150):
        n = rng.choice([1, 2, 3, 8, 24])
        a = vec(n, rng.choice([1.0, 1e-3, 1e3]))
        b = list(a) if i % 10 == 0 else vec(n)
        if i % 13 == 0:
            b = [-x for x in a]
        cosine.append({'input': {'a': a, 'b': b}, 'output': enc(float(calculate_cosine_similarity(a, b)))})

    norm = []
    for v in [[], [0.0], [0.0, 0.0, 0.0], [3.0, 4.0], [-1.0], [1e-38, 0.0], [f32(3e38), f32(3e38)]]:
        v = [f32(x) for x in v]
        norm.append({'input': {'v': v}, 'output': enc(normalize_l2(v))})
    for _ in range(60):
        v = vec(rng.choice([1, 2, 5, 16]), rng.choice([1.0, 1e-4, 1e4]))
        norm.append({'input': {'v': v}, 'output': enc(normalize_l2(v))})

    rrf_cases = []
    pool = [f'u{i}' for i in range(12)] + ['é', '😀', 'Ω', 'a b']
    fixed_rrf = [
        ([], 1, 0),
        ([[]], 1, 0),
        ([['a']], 1, 0),
        ([['a', 'b'], ['b', 'a']], 1, 0),  # tie: first seen wins
        ([['a', 'b', 'c'], ['c', 'b', 'a'], ['b']], 1, 0),
        ([['a', 'a', 'b']], 1, 0),  # repeated id in one ranking
        ([['a', 'b']], 0, 0),  # 1 / 0
        ([['a', 'b', 'c']], -1, 0),  # 1 / 0 at rank 1
        ([['a', 'b', 'c']], -5, -10),
        ([['a', 'b', 'c']], 60, 0),
        ([['a', 'b', 'c'], ['c']], 1, 1.0),  # min_score equal to a score
        ([['a', 'b', 'c'], ['c']], 1, 1.3333333333333333),
        ([['a', 'b', 'c']], 1, 0.5),
        ([['a', 'b', 'c']], 1, 2.0),
    ]
    for results, k, m in fixed_rrf:
        out = call(rrf, results, rank_const=k, min_score=m)
        rrf_cases.append({'input': {'results': results, 'rank_const': k, 'min_score': m}, 'output': enc(out)})
    for _ in range(200):
        results = [
            [rng.choice(pool) for _ in range(rng.randint(0, 8))] for _ in range(rng.randint(0, 5))
        ]
        k = rng.choice([1, 1, 1, 2, 10, 60])
        out_all = rrf(results, rank_const=k)
        if out_all[1] and rng.random() < 0.5:
            m = rng.choice(out_all[1])  # exactly a score: tests >=
        else:
            m = rng.choice([0, 0, 0.1, 0.5, -1.0])
        rrf_cases.append(
            {'input': {'results': results, 'rank_const': k, 'min_score': m}, 'output': enc(rrf(results, k, m))}
        )

    mmr_cases = []

    def mmr_case(query, candidates, lam, min_score):
        out = call(maximal_marginal_relevance, query, dict(candidates), lam, min_score)
        mmr_cases.append(
            {
                'input': {
                    'query': query,
                    'candidates': [[k, v] for k, v in candidates],
                    'mmr_lambda': lam,
                    'min_score': min_score,
                },
                'output': enc(out),
            }
        )

    mmr_case([1.0, 0.0], [], 0.5, -2.0)
    mmr_case([1.0, 0.0], [('a', [1.0, 0.0])], 0.5, -2.0)
    mmr_case([1.0, 0.0], [('a', [0.0, 0.0]), ('b', [0.0, 0.0])], 0.5, -2.0)
    mmr_case([1.0, 0.0], [('a', [1.0, 0.0]), ('b', [1.0, 0.0]), ('c', [0.0, 1.0])], 0.5, -2.0)
    mmr_case([1.0, 0.0], [('a', [1.0, 0.0]), ('b', [2.0, 0.0])], 1.0, -2.0)
    mmr_case([1.0, 0.0], [('a', [1.0, 0.0]), ('b', [-1.0, 0.0])], 0.0, -2.0)
    mmr_case([1.0, 0.0, 0.0], [('a', [1.0, 0.0])], 0.5, -2.0)  # length mismatch
    tries = 0
    while len(mmr_cases) < 160:
        tries += 1
        n = rng.choice([2, 3, 8, 16])
        count = rng.randint(1, 10)
        query = vec(n)
        candidates = []
        for j in range(count):
            if candidates and rng.random() < 0.15:
                candidates.append((f'c{j}', list(rng.choice(candidates)[1])))  # duplicate embedding: exact tie
            elif rng.random() < 0.05:
                candidates.append((f'c{j}', [0.0] * n))
            else:
                candidates.append((f'c{j}', vec(n, rng.choice([1.0, 1e-2, 1e2]))))
        lam = rng.choice([0.5, 0.5, 0.0, 1.0, 0.3, 0.9])
        _, scores = maximal_marginal_relevance(query, dict(candidates), lam, -2.0)
        ordered = sorted(scores)
        # The port may differ from NumPy in the last bits, so skip near (but not exact) ties: their order could flip.
        if any(0 < b - a < 1e-9 * max(1.0, abs(a)) for a, b in zip(ordered, ordered[1:])):
            continue
        min_score = -2.0
        if rng.random() < 0.4 and scores:
            pick = rng.choice(scores)
            min_score = pick - 1e-6 if rng.random() < 0.5 else pick + 1e-6
        mmr_case(query, candidates, lam, min_score)

    write(
        'search_utils',
        [
            'graphiti_core.search.search_utils.calculate_cosine_similarity',
            'rrf',
            'maximal_marginal_relevance',
            'graphiti_core.helpers.normalize_l2',
        ],
        {
            'calculate_cosine_similarity': cosine,
            'normalize_l2': norm,
            'rrf': rrf_cases,
            'maximal_marginal_relevance': mmr_cases,
        },
    )


# ---------------------------------------------------------------------------------------------------------------
# dedup_helpers.py

DEDUP_NAMES = [
    '', ' ', 'a', 'ab', 'abc', 'aaaa', 'aabc', 'abcdef', 'ab cd', 'a b', 'Alice Smith', 'alice  smith', 'ALICE\tSMITH',
    'Alice Smith.', 'Alice Smyth', 'Acme Corporation', 'Acme Corp', 'Acme Corporation Inc', "O'Brien & Sons",
    "o'brien and sons", 'Jean-Luc Picard', 'Jean Luc Picard', 'İstanbul Office', 'istanbul office', 'ΟΔΟΣ ΑΘΗΝΑΣ',
    'οδος αθηνας', 'Straße', 'STRASSE', 'ﬁle server', 'file server', 'Kelvin Scale', 'محمد علي', 'مُحَمَّد علي',
    'شركة القاهرة', 'Café Naïve', 'cafe naive', '東京タワー', '😀😀😀', 'Rocket 🚀 Labs', 'Rocket Labs',
    'a\x1cb\x1fc', 'x\xa0y　z', 'line break', '  padded  ', 'v2.0 release', 'Q3 2024 report',
    'Q3 2024 reports', 'Project Apollo', 'Project Apolo', 'Apollo Project', 'zzzzzzzz', 'abababab', 'Ａｃｍｅ Ｃｏｒｐ',
    '١٢٣ شارع', 'Ĳssel Meer', 'Ǆemal Bijedić', 'ŉ test', 'Iİıi', 'ΣΑΣ', 'σας', 'ǅ', 'ẞ big',
]


def gen_dedup_helpers() -> None:
    rng = random.Random(2002)
    names = list(DEDUP_NAMES) + [rand_name(rng, 0.25) for _ in range(140)]
    # Random code points from scripts with case (and odd case rules), whitespace and marks, to test str.lower().
    blocks = [(0x41, 0x7E), (0xA0, 0x24F), (0x370, 0x3FF), (0x400, 0x52F), (0x531, 0x587), (0x10A0, 0x10FF),
              (0x13A0, 0x13FD), (0x1E00, 0x1FFF), (0x2160, 0x2188), (0x24B6, 0x24E9), (0x2C00, 0x2C5F),
              (0xA640, 0xA69F), (0xFF21, 0xFF5A), (0x10400, 0x1044F), (0x1E900, 0x1E943), (0x1C, 0x20),
              (0x300, 0x36F), (0x2000, 0x200A)]
    for _ in range(150):
        chars = []
        for _ in range(rng.randint(1, 8)):
            lo, hi = rng.choice(blocks)
            chars.append(chr(rng.randint(lo, hi)))
        names.append(''.join(chars))

    exact, fuzzy, entropy, high, shingles = [], [], [], [], []
    for name in names:
        exact.append({'input': name, 'output': dedup_helpers._normalize_string_exact(name)})
        fuzzy.append({'input': name, 'output': dedup_helpers._normalize_name_for_fuzzy(name)})
        for candidate in dict.fromkeys([name, dedup_helpers._normalize_name_for_fuzzy(name)]):
            entropy.append({'input': candidate, 'output': dedup_helpers._name_entropy(candidate)})
            high.append({'input': candidate, 'output': dedup_helpers._has_high_entropy(candidate)})
            shingles.append({'input': candidate, 'output': enc(dedup_helpers._shingles(candidate))})

    minhash = []
    for name in names[:120]:
        s = dedup_helpers._shingles(dedup_helpers._normalize_name_for_fuzzy(name))
        minhash.append({'input': sorted(s), 'output': enc(dedup_helpers._minhash_signature(s))})
    odd = {'😀😀😀', 'محم', 'a b', '\x00ab'}
    minhash.append({'input': sorted(odd), 'output': enc(dedup_helpers._minhash_signature(odd))})

    bands = []
    for length in [0, 1, 3, 4, 5, 7, 8, 9, 12, 31, 32, 33]:
        sig = [rng.getrandbits(64) for _ in range(length)]
        bands.append({'input': sig, 'output': enc(dedup_helpers._lsh_bands(sig))})

    jaccard = []
    pairs = [(set(), set()), ({'abc'}, set()), (set(), {'abc'}), ({'abc'}, {'abc'}), ({'abc', 'bcd'}, {'bcd'})]
    for _ in range(80):
        a = dedup_helpers._shingles(dedup_helpers._normalize_name_for_fuzzy(rng.choice(names)))
        b = dedup_helpers._shingles(dedup_helpers._normalize_name_for_fuzzy(rng.choice(names)))
        pairs.append((a, b))
    for a, b in pairs:
        jaccard.append({'input': [sorted(a), sorted(b)], 'output': dedup_helpers._jaccard_similarity(a, b)})

    # End to end: _build_candidate_indexes + _resolve_with_similarity.
    def mutate(name: str) -> str:
        choice = rng.random()
        if choice < 0.25:
            return name.upper()
        if choice < 0.45:
            return '  ' + name.replace(' ', '\t') + ' '
        if choice < 0.7 and len(name) > 3:
            i = rng.randrange(len(name))
            return name[:i] + rng.choice('aeiouxyz') + name[i + 1 :]
        if choice < 0.85:
            return name + rng.choice(['s', ' Inc', '.', ' 2'])
        return name

    base_names = [n for n in names if n]
    resolve = []
    ambiguous_count = 0
    for case_index in range(160):
        existing_count = rng.randint(0, 8)
        existing_names = [rng.choice(base_names) for _ in range(existing_count)]
        if existing_count and rng.random() < 0.3:
            existing_names.append(existing_names[0])  # two candidates with the same normalized name
        labels_pool = [['Entity'], ['Entity', 'Person'], ['Entity', 'Organization'], ['Entity', 'Person', 'Employee']]
        existing = [
            {
                'uuid': f'e{case_index}-{i}',
                'name': n,
                'labels': rng.choice(labels_pool),
                'attributes': rng.choice([{}, {'role': 'cto'}, {'city': 'Cairo', 'age': 40}]),
            }
            for i, n in enumerate(existing_names)
        ]
        extracted = []
        for i in range(rng.randint(1, 6)):
            r = rng.random()
            if existing and r < 0.55:
                n = mutate(rng.choice(existing)['name'])
            elif existing and r < 0.65:
                n = rng.choice(existing)['name']
            else:
                n = rng.choice(base_names)
            extracted.append(
                {
                    'uuid': rng.choice([f'x{case_index}-{i}'] + [e['uuid'] for e in existing][:1]),
                    'name': n,
                    'labels': rng.choice(labels_pool),
                    'attributes': rng.choice([{}, {'role': 'ceo'}, {'role': None, 'city': ' '}, {'age': 41}]),
                }
            )

        def make(d):
            return EntityNode(
                uuid=d['uuid'], name=d['name'], group_id='g', labels=list(d['labels']),
                attributes=dict(d['attributes']), created_at=datetime(2024, 1, 1, tzinfo=UTC),
            )

        existing_nodes = [make(d) for d in existing]
        extracted_nodes = [make(d) for d in extracted]
        indexes = dedup_helpers._build_candidate_indexes(existing_nodes)

        # Upstream visits LSH candidates in set order, which changes between runs. Note which extracted nodes
        # have more than one best candidate; for those any of the tied candidates is a correct answer.
        allowed: dict[int, list[str]] = {}
        for idx, node in enumerate(extracted_nodes):
            exact_name = dedup_helpers._normalize_string_exact(node.name)
            if indexes.normalized_existing.get(exact_name):
                continue
            fz = dedup_helpers._normalize_name_for_fuzzy(node.name)
            if not dedup_helpers._has_high_entropy(fz):
                continue
            sh = dedup_helpers._shingles(fz)
            ids = set()
            for bi, band in enumerate(dedup_helpers._lsh_bands(dedup_helpers._minhash_signature(sh))):
                ids.update(indexes.lsh_buckets.get((bi, band), []))
            scored = [(dedup_helpers._jaccard_similarity(sh, indexes.shingles_by_candidate[c]), c) for c in ids]
            if not scored:
                continue
            best = max(s for s, _ in scored)
            tied = sorted({c for s, c in scored if s == best})
            if best >= dedup_helpers._FUZZY_JACCARD_THRESHOLD and len(tied) > 1:
                allowed[idx] = tied

        state = dedup_helpers.DedupResolutionState(
            resolved_nodes=[None] * len(extracted_nodes), uuid_map={}, unresolved_indices=[]
        )
        dedup_helpers._resolve_with_similarity(extracted_nodes, indexes, state)
        output = {
            'resolved': [n.uuid if n is not None else None for n in state.resolved_nodes],
            'uuid_map': state.uuid_map,
            'unresolved_indices': state.unresolved_indices,
            'duplicate_pairs': [[a.uuid, b.uuid] for a, b in state.duplicate_pairs],
            'existing_after': [
                {'uuid': n.uuid, 'labels': n.labels, 'attributes': enc(n.attributes)} for n in existing_nodes
            ],
            # Indexes whose answer depends on Python's set order, with every acceptable candidate.
            'order_dependent': {str(k): v for k, v in allowed.items()},
        }
        if allowed:
            # Keep the file the same from run to run: drop what the set order decided.
            ambiguous_count += 1
            for k in allowed:
                output['resolved'][k] = None
            for key in ('uuid_map', 'duplicate_pairs', 'existing_after'):
                del output[key]
        resolve.append({'input': {'existing': existing, 'extracted': extracted}, 'output': output})

    write(
        'dedup_helpers',
        [
            'graphiti_core.utils.maintenance.dedup_helpers._normalize_string_exact',
            '_normalize_name_for_fuzzy',
            '_name_entropy',
            '_has_high_entropy',
            '_shingles',
            '_minhash_signature',
            '_lsh_bands',
            '_jaccard_similarity',
            '_build_candidate_indexes',
            '_resolve_with_similarity',
        ],
        {
            'normalize_string_exact': exact,
            'normalize_name_for_fuzzy': fuzzy,
            'name_entropy': entropy,
            'has_high_entropy': high,
            'shingles': shingles,
            'minhash_signature': minhash,
            'lsh_bands': bands,
            'jaccard_similarity': jaccard,
            'resolve_with_similarity': resolve,
        },
    )
    print(f'  resolve_with_similarity: {ambiguous_count} cases with set-order-dependent ties')


# ---------------------------------------------------------------------------------------------------------------
# edge_operations.py

# Instants with ties across offsets: T[1] and T1_OFFSET are the same instant.
T = [
    datetime(2024, 1, 1, 0, 0, tzinfo=UTC),
    datetime(2024, 3, 1, 12, 0, tzinfo=UTC),
    datetime(2024, 6, 1, 0, 0, 0, 500000, tzinfo=UTC),
    datetime(2025, 1, 1, tzinfo=UTC),
]
T1_OFFSET = datetime(2024, 3, 1, 17, 30, tzinfo=timezone(timedelta(hours=5, minutes=30)))
T2_NAIVE = datetime(2024, 6, 1, 0, 0, 0, 500000)
PRESET_EXPIRED = datetime(2023, 12, 31, 23, 59, tzinfo=timezone(timedelta(hours=-8)))
TIME_CHOICES = [None, T[0], T[1], T1_OFFSET, T[2], T2_NAIVE, T[3]]


def make_edge(uuid, valid_at=None, invalid_at=None, expired_at=None, name='RELATES_TO', attributes=None):
    return EntityEdge(
        uuid=uuid, group_id='g', source_node_uuid='s', target_node_uuid='t', created_at=T[0], name=name,
        fact=f'fact {uuid}', valid_at=valid_at, invalid_at=invalid_at, expired_at=expired_at,
        attributes=attributes or {},
    )


def edge_state(e: EntityEdge):
    return {
        'uuid': e.uuid,
        'valid_at': enc(e.valid_at),
        'invalid_at': enc(e.invalid_at),
        'expired_at': enc(e.expired_at),
    }


def gen_edge_operations() -> None:
    rng = random.Random(3003)
    edge_operations.utc_now = lambda: FROZEN_NOW  # freeze "now" so the fixtures can say where it was set

    contradictions, temporal = [], []
    single_times = [None, T[0], T[1], T1_OFFSET, T[3]]
    # Every combination of one resolved edge and one candidate.
    for rv in single_times:
        for ri in single_times:
            for cv in single_times:
                for ci in single_times:
                    for cexp in [None, PRESET_EXPIRED]:
                        for fn, sink in ((edge_operations.resolve_edge_contradictions, contradictions),
                                         (edge_operations._apply_temporal_invalidation, temporal)):
                            for rexp in ([None] if fn is edge_operations.resolve_edge_contradictions
                                         else [None, PRESET_EXPIRED]):
                                if cexp is not None and rexp is not None:
                                    continue  # both preset: covered by the random sets below
                                resolved = make_edge('r', rv, ri, rexp)
                                cand = make_edge('c0', cv, ci, cexp)
                                inp = {'resolved': edge_state(resolved), 'candidates': [edge_state(cand)]}
                                cands = [cand]
                                result = fn(resolved, cands)
                                sink.append({
                                    'input': inp,
                                    'output': {
                                        'invalidated': [e.uuid for e in result],
                                        'resolved': edge_state(resolved),
                                        'candidates': [edge_state(e) for e in cands],
                                    },
                                })
    # Random multi-candidate sets (sorting, ties, naive datetimes).
    for i in range(150):
        for fn, sink in ((edge_operations.resolve_edge_contradictions, contradictions),
                         (edge_operations._apply_temporal_invalidation, temporal)):
            resolved = make_edge('r', rng.choice(TIME_CHOICES), rng.choice(TIME_CHOICES),
                                 rng.choice([None, None, PRESET_EXPIRED]))
            cands = [
                make_edge(f'c{j}', rng.choice(TIME_CHOICES), rng.choice(TIME_CHOICES),
                          rng.choice([None, None, PRESET_EXPIRED]))
                for j in range(rng.randint(0, 6))
            ]
            inp = {'resolved': edge_state(resolved), 'candidates': [edge_state(e) for e in cands]}
            result = fn(resolved, cands)
            sink.append({
                'input': inp,
                'output': {
                    'invalidated': [e.uuid for e in result],
                    'resolved': edge_state(resolved),
                    'candidates': [edge_state(e) for e in cands],
                },
            })

    # enforce_edge_type_signatures and _edge_types_for_endpoints
    enforce, endpoints = [], []
    type_names = ['WORKS_AT', 'LIVES_IN', 'KNOWS', 'OWNS', 'RELATES_TO', 'Ünïcode_TYPE']
    labels = ['Entity', 'Person', 'Company', 'Place', 'Ünïcode_Label_Unused', 'Other_1']
    for i in range(150):
        edge_type_map = {}
        for _ in range(rng.randint(0, 4)):
            key = (rng.choice(labels), rng.choice(labels))
            edge_type_map[key] = rng.sample(type_names, rng.randint(0, 3))
        edge_types = {n: BaseModel for n in rng.sample(type_names, rng.randint(0, len(type_names)))}
        nodes = {
            f'n{j}': EntityNode(uuid=f'n{j}', name=f'n{j}', group_id='g',
                                labels=rng.sample([lb for lb in labels[1:] if lb.isascii()], rng.randint(0, 2)))
            for j in range(3)
        }
        edges = []
        for j in range(rng.randint(0, 5)):
            e = make_edge(f'e{j}', name=rng.choice(type_names),
                          attributes=rng.choice([{}, {'since': 2020}, {'role': 'x'}]))
            e.source_node_uuid = rng.choice(list(nodes) + ['missing'])
            e.target_node_uuid = rng.choice(list(nodes) + ['missing'])
            edges.append(e)
        node_json = [{'uuid': n.uuid, 'labels': n.labels} for n in nodes.values()]
        map_json = [[k[0], k[1], v] for k, v in edge_type_map.items()]
        edge_json = [{'uuid': e.uuid, 'name': e.name, 'source': e.source_node_uuid, 'target': e.target_node_uuid,
                      'attributes': e.attributes} for e in edges]
        matching = [edge_operations._edge_types_for_endpoints(e, nodes, edge_types, edge_type_map) for e in edges]
        for e, m in zip(edges, matching):
            endpoints.append({
                'input': {'nodes': node_json, 'edge': {'source': e.source_node_uuid, 'target': e.target_node_uuid},
                          'edge_types': list(edge_types), 'edge_type_map': map_json},
                'output': list(m),
            })
        strict = rng.random() < 0.5
        kept, kept_types = edge_operations.enforce_edge_type_signatures(edges, matching, edge_type_map, strict)
        enforce.append({
            'input': {'edges': edge_json, 'matching': [list(m) for m in matching], 'edge_type_map': map_json,
                      'strict': strict},
            'output': {
                'kept': [{'uuid': e.uuid, 'name': e.name, 'attributes': e.attributes} for e in kept],
                'kept_types': [list(m) for m in kept_types],
            },
        })

    write(
        'edge_operations',
        [
            'graphiti_core.utils.maintenance.edge_operations.resolve_edge_contradictions',
            '_apply_temporal_invalidation',
            'enforce_edge_type_signatures',
            '_edge_types_for_endpoints',
        ],
        {
            'resolve_edge_contradictions': contradictions,
            'apply_temporal_invalidation': temporal,
            'enforce_edge_type_signatures': enforce,
            'edge_types_for_endpoints': endpoints,
        },
    )


# ---------------------------------------------------------------------------------------------------------------
# temporal_edge_utils.py

FUTURE_PHRASES = [
    'will', 'WILL', 'Will', 'shall', 'is going to', 'are going to', 'going to', 'plans to', 'plan to', 'planned to',
    'planning to', 'intends to', 'intend to', 'intended to', 'expects to', 'expected to', 'promises to', 'promised to',
    'scheduled to', 'is scheduled to', 'set to', 'aims to', 'aimed to', 'committed to', "we'll", "THEY'LL", "she'll",
    'it’ll', "'ll", "x'llx", 'willing to', 'goodwill', 'will_', 'ًwill', 'will١', 'wiłl', 'ſhall', "Kelvin'll",
    "İt'll", 'went', 'had', 'is', 'was planned', 'sets to',
]
DEADLINE_PHRASES = ['by', 'BY', 'before', 'no later than', 'deadline', 'due by', 'due on', 'bypass', 'nearby', '', '']
ONGOING_PHRASES = ['until', 'through', 'thru', 'expires', 'expire', 'expired', 'ends', 'end', 'ended', 'ending',
                   'stops', 'stop', 'stopped', 'ceases', 'cease', 'ceased', 'no longer', 'Until', 'endless', '', '', '']



def fuzz_iso_strings(rng: random.Random, count: int) -> list[str]:
    """Timestamps assembled from the pieces fromisoformat knows, valid and broken, with random damage."""
    dates = ['2024-01-02', '20240102', '2024-W01', '2024W01', '2024-W01-3', '2024W013', '2020-W53', '2021-W53-1',
             '9999-12-31', '0001-01-01', '2024-02-29', '2023-02-29', '2024-00-10', '2024-12-32', '2024-1-02',
             '2024-0102', '202401-02', '0000-01-01', '9999-W52-6', '9999-W52-7', '2024-W1', '2024-W01-0']
    seps = ['T', ' ', 't', 'X', '_', '0', '-', 'é', '😀', '\x00', '\u3000', '']
    times = ['', '03', '0304', '03:04', '030405', '03:04:05', '03:04:05.1', '03:04:05,123', '030405.123456789',
             '03:04:05.', '03:04.5', '0304.5', '03.5', '24:00', '23:59:60', '3:04', '03:4', '03:04:05:06', '0304057',
             '03:0405', '0304:05', '03:04:05.12a', '00:00']
    zones = ['', '', 'Z', '+00:00', '-00:00', '+05:30', '+0530', '+05', '-08:00', '+23:59', '+24:00', '-23:59:59',
             '+05:30:15', '+053015', '+05:30:15.5', '+05:30:15.123456789', '+5', '+05:3', '+05:99', '+99:00', 'ZZ',
             'Z+01:00', '+01:00Z', '+', '-', '+01:00\x00', '+00:00:00.000001', '-00:00:00.000001']
    out = []
    for _ in range(count):
        text = rng.choice(dates) + rng.choice(seps) + rng.choice(times) + rng.choice(zones)
        damage = rng.random()
        if damage < 0.08 and text:
            i = rng.randrange(len(text))
            text = text[:i] + text[i + 1 :]
        elif damage < 0.14:
            i = rng.randrange(len(text) + 1)
            text = text[:i] + rng.choice('0123456789-:+.TZW ') + text[i:]
        out.append(text)
    return out


def gen_temporal_edge_utils() -> None:
    rng = random.Random(4004)
    times = [None, T[0], T[1], T1_OFFSET, T[2], T2_NAIVE, T[3],
             datetime(2024, 3, 1, 12, 0, tzinfo=timezone(timedelta(hours=-14))),
             datetime(2024, 3, 2, 1, 59, tzinfo=timezone(timedelta(hours=14)))]

    normalize = []
    for i in range(500):
        fact = ' '.join(
            p for p in [
                rand_word(rng, 0.2).capitalize(),
                rng.choice(FUTURE_PHRASES),
                rand_word(rng, 0.2),
                rng.choice(DEADLINE_PHRASES),
                rng.choice(ONGOING_PHRASES),
                rng.choice(['Friday', 'next year', '2025', 'مارس', '🚀']),
            ] if p
        )
        if i % 25 == 0:
            fact = ''
        ref, va, ia = rng.choice(times), rng.choice(times), rng.choice(times)
        out = temporal_edge_utils.normalize_future_temporal_bounds(fact, ref, va, ia)
        normalize.append({
            'input': {'fact': fact, 'reference_time': enc(ref), 'valid_at': enc(va), 'invalid_at': enc(ia)},
            'output': enc(list(out)),
        })

    iso_strings = [
        None, '', '2024-01-02', '2024-01-02T03:04', '2024-01-02T03:04:05', '2024-01-02T03:04:05Z',
        '2024-01-02T03:04:05.1Z', '2024-01-02T03:04:05.123456Z', '2024-01-02T03:04:05.1234567Z',
        '2024-01-02T03:04:05.123456789+02:00', '2024-01-02T03:04:05,5+02:00', '2024-01-02 03:04:05+05:30',
        '2024-01-02T03:04:05+0530', '2024-01-02T03:04:05+05', '2024-01-02T03:04:05-08:00', '2024-01-02T03:04:05+00:00',
        '2024-01-02T03:04:05-00:00', '2024-01-02T03:04:05+23:59', '2024-01-02T03:04:05+05:30:15',
        '2024-01-02T03:04:05+05:30:15.5', '2024-01-02T03:04:05+053015', '2024-01-02T03', '2024-01-02T0304',
        '2024-01-02T030405', '2024-01-02T030405.25', '20240102', '20240102T030405', '20240102T030405Z',
        '2024-W01', '2024-W01-3', '2024W013', '2024W01', '2024-W53-1', '2020-W53-7', '2024-W00-1', '2024-W01-8',
        '2024-01-02X03:04:05', '2024-01-02é03:04', '2024-01-02😀03:04', '2024-01-02T03:04:05ZZ',
        '2024-01-02T03:04:05z', '2024-1-2', '24-01-02', '2024/01/02', '2024-13-01', '2024-02-30', '2023-02-29',
        '2024-02-29', '2024-01-02T24:00:00', '2024-01-02T23:59:60', '2024-01-02T25:00', '2024-01-02T03:04:05.',
        '2024-01-02T03:04:05.abc', '2024-01-02T03:04:05+', '2024-01-02T03:04:05+5', '2024-01-02T03:04:05+24:00',
        '2024-01-02T03:04:05 +02:00', ' 2024-01-02', '2024-01-02 ', '2024-01-02T', '٢٠٢٤-٠١-٠٢',
        '2024-01-02T03:04:05Z+01:00',
        'Z', 'not a date', '2024-01-02T03:04:05.000000', '0001-01-01T00:00:00', '0001-01-01T00:00:00+01:00',
        '9999-12-31T23:59:59.999999', '9999-12-31T23:59:59-01:00', '2024-01-02T03:04:05+00:00:00.000001',
        '2024-01-02T03:04:05.123456+05:30', '2024-01-02T03:04-05:00', '2024-01-02T03-05:00', '2024-001',
        '2024-01-02T03:04:05.1234567890123', '2024-01-0203:04', '2024-01-02T03:4', '2024-01-02T3:04',
    ]
    iso_strings += fuzz_iso_strings(rng, 400)

    def parse(text):
        """What upstream does with one timestamp string: a UTC datetime, or the exception it raises."""
        try:
            return ensure_utc(datetime.fromisoformat(text.replace('Z', '+00:00')))
        except (ValueError, OverflowError) as exc:
            return {'$raises': type(exc).__name__}

    fromiso = [{'input': text, 'output': enc(parse(text))} for text in iso_strings if text]

    apply_cases = []
    references = [None, T[1], T1_OFFSET, T2_NAIVE]
    existing = [None, T[0], T[3]]
    for vs in iso_strings[:120]:
        for ivs in [None, '2024-12-31T00:00:00Z', vs]:
            for fact in ['Alice works at Acme', 'Alice will join Acme by Friday', 'The lease will run until June']:
                ref = rng.choice(references)
                pre_valid, pre_invalid = rng.choice(existing), rng.choice(existing)

                def run(valid_text, invalid_text):
                    edge = make_edge('e', pre_valid, pre_invalid)
                    edge.fact = fact
                    temporal_edge_utils.apply_extracted_timestamps(edge, valid_text, invalid_text, ref)
                    return {'valid_at': enc(edge.valid_at), 'invalid_at': enc(edge.invalid_at)}

                case = {
                    'input': {
                        'fact': fact, 'valid_at': vs, 'invalid_at': ivs, 'reference_time': enc(ref),
                        'edge_valid_at': enc(pre_valid), 'edge_invalid_at': enc(pre_invalid),
                    },
                }
                try:
                    case['output'] = run(vs, ivs)
                except OverflowError:
                    # Deliberate deviation: upstream lets OverflowError escape when a timestamp's UTC conversion leaves
                    # the datetime range; the port skips such a value as unparseable. Record that expectation.
                    def keep(text):
                        return None if text and isinstance(parse(text), dict) and \
                            parse(text)['$raises'] == 'OverflowError' else text

                    case['deviation'] = 'upstream raises OverflowError; the port skips the out-of-range timestamp'
                    case['output'] = run(keep(vs), keep(ivs))
                apply_cases.append(case)

    write(
        'temporal_edge_utils',
        [
            'graphiti_core.utils.maintenance.temporal_edge_utils.normalize_future_temporal_bounds',
            'apply_extracted_timestamps',
            'datetime.fromisoformat (Python 3.13) with ensure_utc',
        ],
        {
            'normalize_future_temporal_bounds': normalize,
            'fromisoformat': fromiso,
            'apply_extracted_timestamps': apply_cases,
        },
    )


# ---------------------------------------------------------------------------------------------------------------
# text_utils.py

SENTENCE_ENDS = ['.', '!', '?', '...', '?!', '.　', '.\x1c', '.\xa0', '. ', '。', '؟', '.)']


def rand_text(rng: random.Random, max_sentences: int = 6) -> str:
    parts = []
    for _ in range(rng.randint(0, max_sentences)):
        sentence = ' '.join(rand_word(rng, 0.35) for _ in range(rng.randint(1, 8)))
        parts.append(sentence + rng.choice(SENTENCE_ENDS + ['', '']))
    return ''.join(p + rng.choice(SPACES + ['', '']) for p in parts)


def gen_text_utils() -> None:
    rng = random.Random(5005)

    sentence = []
    fixed = [('', 5), ('Hello.', 0), ('Hello. World.', 6), ('Hello. World.', 7), ('Hello. World.', 100),
             ('Hello world', 5), ('Hi! There? Yes.', 4), ('a😀b😀c. d', 4), ('a😀b. 😀😀😀', 5), ('a.\x1cb', 2),
             ('abc   ', 4), ('abc\x1c\x1c', 4), ('Hello. World.', -1), ('Hello. World.', -7), ('x', -5)]
    for text, n in fixed:
        sentence.append({'input': {'text': text, 'max_chars': n}, 'output': text_utils.truncate_at_sentence(text, n)})
    for _ in range(300):
        text = rand_text(rng)
        n = rng.choice([0, 1, 5, 10, 20, 40, 80, 200, max(0, len(text) - 1), len(text)])
        sentence.append({'input': {'text': text, 'max_chars': n}, 'output': text_utils.truncate_at_sentence(text, n)})

    utf8 = []
    for text in ['', 'abc', 'é', 'محمد', '😀', 'a😀b', '👩‍💻x', '東京', 'ü' * 5, '\x00a']:
        for n in [-1, 0, 1, 2, 3, 4, 5, 6, 7, 100]:
            utf8.append({'input': {'text': text, 'max_bytes': n}, 'output': text_utils.truncate_utf8_to_bytes(text, n)})
    for _ in range(150):
        text = rand_text(rng, 3)
        n = rng.randint(0, len(text.encode()) + 2)
        utf8.append({'input': {'text': text, 'max_bytes': n}, 'output': text_utils.truncate_utf8_to_bytes(text, n)})

    offsets = [UTC, timezone(timedelta(hours=5, minutes=30)), timezone(timedelta(hours=-8)),
               timezone(timedelta(hours=-3, minutes=-30))]

    def episode(i):
        valid_at = datetime(2024, rng.randint(1, 12), rng.randint(1, 28), rng.randint(0, 23), rng.randint(0, 59),
                            rng.randint(0, 59), rng.choice([0, 0, 1, 500000, 123456]), tzinfo=rng.choice(offsets))
        return EpisodicNode(uuid=f'ep{i}', name=f'ep{i}', group_id='g', source=EpisodeType.message,
                            source_description='chat', content=rand_text(rng, 3), valid_at=valid_at)

    def ep_json(ep):
        return {'uuid': ep.uuid, 'content': ep.content, 'valid_at': enc(ep.valid_at)}

    episodes_cases, timeline_cases = [], []
    for i in range(120):
        eps = [episode(j) for j in range(rng.choice([0, 1, 1, 2, 3, 5]))]
        if eps and rng.random() < 0.15:
            eps.append(eps[0])  # the same episode twice
        episodes_cases.append({'input': [ep_json(e) for e in eps], 'output': text_utils.concatenate_episodes(eps)})
        context = [episode(100 + j) for j in range(rng.randint(0, 3))]
        timeline = eps + context
        rng.shuffle(timeline)
        timeline_cases.append({
            'input': {'episodes': [ep_json(e) for e in eps], 'timeline': [ep_json(e) for e in timeline]},
            'output': text_utils.concatenate_timeline(eps, timeline),
        })

    write(
        'text_utils',
        [
            'graphiti_core.utils.text_utils.truncate_at_sentence',
            'truncate_utf8_to_bytes',
            'concatenate_episodes',
            'concatenate_timeline',
        ],
        {
            'truncate_at_sentence': sentence,
            'truncate_utf8_to_bytes': utf8,
            'concatenate_episodes': episodes_cases,
            'concatenate_timeline': timeline_cases,
        },
    )


# ---------------------------------------------------------------------------------------------------------------
# bulk_utils.py


def gen_bulk_utils() -> None:
    rng = random.Random(6006)
    # Ids whose UTF-16 order differs from their code-point order ('Ａ' sorts after '😀' in UTF-16).
    pool = ['a', 'b', 'c', 'd', 'e', 'f', 'A', 'B', 'aa', 'Ａ', '😀', 'é', 'z', '0', '9', 'uuid-1', 'uuid-10',
            'uuid-2']

    compress, directed = [], []
    fixed = [[], [('a', 'a')], [('a', 'b')], [('b', 'a')], [('Ａ', '😀')], [('😀', 'Ａ')],
             [('a', 'b'), ('b', 'c'), ('c', 'a')], [('c', 'b'), ('b', 'a'), ('d', 'c')]]
    for pairs in fixed + [
        [(rng.choice(pool), rng.choice(pool)) for _ in range(rng.randint(0, 10))] for _ in range(150)
    ]:
        # compress_uuid_map builds its map from a set; sort it so the file is the same on every run.
        compress.append({'input': [list(p) for p in pairs],
                         'output': dict(sorted(bulk_utils.compress_uuid_map(pairs).items()))})
        directed.append({'input': [list(p) for p in pairs], 'output': bulk_utils._build_directed_uuid_map(pairs)})

    pointers = []
    for _ in range(150):
        edges = [make_edge(f'e{j}') for j in range(rng.randint(0, 6))]
        for e in edges:
            e.source_node_uuid = rng.choice(pool[:8])
            e.target_node_uuid = rng.choice(pool[:8]) if rng.random() < 0.8 else e.source_node_uuid
        uuid_map = {rng.choice(pool[:8]): rng.choice(pool[:8]) for _ in range(rng.randint(0, 5))}
        inp = {'edges': [[e.uuid, e.source_node_uuid, e.target_node_uuid] for e in edges], 'uuid_map': uuid_map}
        kept = bulk_utils.resolve_edge_pointers(edges, uuid_map)
        pointers.append({
            'input': inp,
            'output': {
                'kept': [e.uuid for e in kept],
                'edges': [[e.uuid, e.source_node_uuid, e.target_node_uuid] for e in edges],
            },
        })

    write(
        'bulk_utils',
        ['graphiti_core.utils.bulk_utils.compress_uuid_map', '_build_directed_uuid_map', 'resolve_edge_pointers'],
        {'compress_uuid_map': compress, 'build_directed_uuid_map': directed, 'resolve_edge_pointers': pointers},
    )


# ---------------------------------------------------------------------------------------------------------------
# attribute_utils.py

# The C# test declares the same types (Differential/AttributeModels.cs); keep them in step.


class DiffPerson(BaseModel):
    full_name: str
    phones: str | None = Field(default=None, max_length=20)
    aliases: list[str] | None = None
    industry: str | None = None
    notes: str | None = Field(default=None, max_length=0)
    tags: list[str]
    bio: str = Field(default='', max_length=5)


class DiffEmpty(BaseModel):
    pass


ATTRIBUTE_MODELS = {'DiffPerson': DiffPerson, 'DiffEmpty': DiffEmpty}


def gen_attribute_utils() -> None:
    rng = random.Random(7007)
    keys = ['full_name', 'phones', 'aliases', 'industry', 'notes', 'tags', 'bio', 'extra', 'count']

    def rand_string(cap):
        length = rng.choice([0, 1, cap - 1, cap, cap + 1, cap * 2, 3, 30]) if cap > 0 else rng.choice([0, 1, 5])
        length = max(0, length)
        unit = rng.choice(['x', 'é', '😀', 'م', ' '])
        return unit * length

    def rand_value(cap):
        r = rng.random()
        if r < 0.45:
            return rand_string(cap)
        if r < 0.75:
            items = []
            for _ in range(rng.randint(0, 10)):
                q = rng.random()
                if q < 0.75:
                    items.append(rand_string(cap))
                elif q < 0.85:
                    items.append(rng.randint(-5, 5))
                elif q < 0.9:
                    items.append(None)
                else:
                    items.append([rand_string(cap)])
            return items
        if r < 0.85:
            return None
        if r < 0.9:
            return rng.choice([True, 3, 2.5, -1])
        return {'nested': rand_string(cap)}

    def stable(merged, dropped):
        # Replace mode appends restored fields in set order; sort them so the file is the same on every run.
        restored = sorted(k for k in merged if k in dropped)
        return {**{k: v for k, v in merged.items() if k not in dropped}, **{k: merged[k] for k in restored}}

    cap_cases, apply_cases = [], []
    for i in range(300):
        model_name = rng.choice(['DiffPerson', 'DiffPerson', 'DiffEmpty'])
        model = ATTRIBUTE_MODELS[model_name]
        default = rng.choice([250, 250, 10, 3, 1, 0, -1])
        cap_hint = default if default > 0 else 5
        response = {k: rand_value(rng.choice([cap_hint, 20, 5])) for k in rng.sample(keys, rng.randint(0, len(keys)))}
        kept, dropped = attribute_utils.cap_string_attributes(response, model, default_max_length=default)
        cap_cases.append({
            'input': {'model': model_name, 'default_max_length': default, 'response': enc(response)},
            'output': {'kept': enc(kept), 'dropped': enc(dropped)},
        })
        prior = {k: rand_value(cap_hint) for k in rng.sample(keys, rng.randint(0, 5))}
        mode = rng.choice(['overlay', 'replace'])
        merged, dropped = attribute_utils.apply_capped_attributes(
            response, model, prior, merge_mode=mode, default_max_length=default
        )
        apply_cases.append({
            'input': {'model': model_name, 'default_max_length': default, 'response': enc(response),
                      'prior': enc(prior), 'merge_mode': mode},
            # Keys restored from the dropped set follow Python set order, so the port compares them as a set.
            'output': {'merged': enc(stable(merged, dropped)), 'dropped': enc(dropped)},
        })

    write(
        'attribute_utils',
        ['graphiti_core.utils.maintenance.attribute_utils.cap_string_attributes', 'apply_capped_attributes'],
        {'cap_string_attributes': cap_cases, 'apply_capped_attributes': apply_cases},
    )


# ---------------------------------------------------------------------------------------------------------------
# prompt_helpers.py


def gen_prompt_helpers() -> None:
    rng = random.Random(8008)
    strings = ['', 'plain', 'quote " and \\ backslash', 'tab\tnew\nline\rcr', '\x00\x01\x1f\x7f', 'محمد علي',
               '😀 emoji 👩‍💻', 'Ελληνικά', '東京', '  ', '﻿bom', 'é', '</script>', '\b\f']
    floats = [0.0, -0.0, 1.0, -1.5, 0.1 + 0.2, 1e16, 1e15, 1e-5, 1e-4, 123456789.123, 1.5e300, 5e-324,
              float('nan'), float('inf'), float('-inf'), 2.5, 1 / 3]
    ints = [0, 1, -1, 2 ** 63 - 1, 2 ** 63, -(2 ** 63) - 1, 10 ** 30, 42]

    def rand_value(depth=0):
        r = rng.random()
        if depth > 3 or r < 0.5:
            pick = rng.random()
            if pick < 0.35:
                return rng.choice(strings)
            if pick < 0.55:
                return rng.choice(floats)
            if pick < 0.75:
                return rng.choice(ints)
            if pick < 0.9:
                return rng.choice([True, False])
            return None
        if r < 0.7:
            return [rand_value(depth + 1) for _ in range(rng.randint(0, 4))]
        if r < 0.75:
            return tuple(rand_value(depth + 1) for _ in range(rng.randint(0, 3)))
        d = {}
        for _ in range(rng.randint(0, 4)):
            k_pick = rng.random()
            if k_pick < 0.8:
                key = rng.choice(strings + ['name', 'summary', 'id'])
            elif k_pick < 0.85:
                key = rng.choice(ints)
            elif k_pick < 0.9:
                key = rng.choice(floats)
            elif k_pick < 0.95:
                key = rng.choice([True, False])
            else:
                key = None
            d[key] = rand_value(depth + 1)
        return d

    cases = []
    for i in range(300):
        value = rand_value()
        ensure_ascii = rng.random() < 0.3
        indent = rng.choice([None, None, None, 0, 1, 2, 4])
        cases.append({
            'input': {'data': enc(value), 'ensure_ascii': ensure_ascii, 'indent': indent},
            'output': to_prompt_json(value, ensure_ascii=ensure_ascii, indent=indent),
        })

    write('prompt_helpers', ['graphiti_core.prompts.prompt_helpers.to_prompt_json'], {'to_prompt_json': cases})


# ---------------------------------------------------------------------------------------------------------------
# content_chunking.py


def gen_content_chunking() -> None:
    rng = random.Random(9009)
    estimate, should, json_chunks, text_chunks, message_chunks, covering = [], [], [], [], [], []

    def big_text(n_sentences):
        text = rand_text(rng, n_sentences)
        if rng.random() < 0.75:
            # Mostly Basic Multilingual Plane text, where the port must match exactly (see astral below).
            text = ''.join(c for c in text if ord(c) <= 0xFFFF)
        return text

    def astral(text):
        # The port counts UTF-16 code units where upstream counts code points (a documented deviation), so sizes
        # differ for text with characters outside the Basic Multilingual Plane, such as emoji.
        return any(ord(c) > 0xFFFF for c in text)

    for _ in range(60):
        text = big_text(rng.choice([0, 3, 30, 300]))
        estimate.append({'input': text, 'astral': astral(text), 'output': content_chunking.estimate_tokens(text)})

    def dense_json(n):
        return json.dumps([{'name': f'Item {i}', 'price': i * 1.5, 'tags': ['a', 'b']} for i in range(n)])

    for _ in range(30):
        kind = rng.choice(['text', 'json', 'message'])
        if kind == 'json':
            content = rng.choice([dense_json(rng.choice([5, 400, 1200])),
                                  json.dumps({f'k{i}': {'v': i} for i in range(rng.choice([10, 700]))}),
                                  '{not json' + 'x' * 5000, '"scalar"' + ' ' * 5000])
        else:
            words = [rng.choice(['Alice', 'Bob', 'Acme', 'the', 'and', 'Paris', 'went', 'NASA', 'Dr.'])
                     for _ in range(rng.choice([10, 1200]))]
            content = ' '.join(words)
        should.append({'input': {'content': content, 'episode_type': kind}, 'astral': astral(content),
                       'output': content_chunking.should_chunk(content, EpisodeType(kind))})

    for _ in range(40):
        content = rng.choice([
            dense_json(rng.randint(0, 60)),
            json.dumps({f'key{i}': 'v' * rng.randint(1, 80) for i in range(rng.randint(0, 40))}),
            json.dumps('scalar'), '[1, 2', json.dumps([]), json.dumps({}),
            json.dumps([{'name': 'محمد', 'emoji': '😀'} for _ in range(30)], ensure_ascii=False),
        ])
        size, overlap = rng.choice([(50, 10), (100, 20), (20, 5), (None, None)])
        json_chunks.append({'input': {'content': content, 'chunk_size_tokens': size, 'overlap_tokens': overlap},
                            'astral': astral(content),
                            'output': content_chunking.chunk_json_content(content, size, overlap)})

    for _ in range(60):
        content = rng.choice([big_text(rng.randint(0, 40)), '\n\n'.join(big_text(4) for _ in range(rng.randint(1, 8))),
                              'x' * rng.randint(0, 900), ' '.join(['word'] * rng.randint(0, 300))])
        size, overlap = rng.choice([(50, 10), (100, 20), (20, 5), (30, 0), (None, None)])
        text_chunks.append({'input': {'content': content, 'chunk_size_tokens': size, 'overlap_tokens': overlap},
                            'astral': astral(content),
                            'output': content_chunking.chunk_text_content(content, size, overlap)})

    speakers = ['Alice', 'Bob', 'محمد', 'Dr. Who', 'user', 'assistant']
    for _ in range(60):
        style = rng.random()
        if style < 0.4:
            content = '\n'.join(f'{rng.choice(speakers)}: {big_text(2)}' for _ in range(rng.randint(0, 40)))
        elif style < 0.6:
            content = json.dumps([{'role': rng.choice(speakers), 'content': big_text(2)}
                                  for _ in range(rng.randint(0, 30))], ensure_ascii=False)
        else:
            content = '\n'.join(big_text(2) for _ in range(rng.randint(0, 40)))
        size, overlap = rng.choice([(50, 10), (100, 20), (20, 5), (None, None)])
        message_chunks.append({'input': {'content': content, 'chunk_size_tokens': size, 'overlap_tokens': overlap},
                               'astral': astral(content),
                            'output': content_chunking.chunk_message_content(content, size, overlap)})

    for n in range(0, 11):
        for k in [1, 2, 3, 4, 5]:
            if math.comb(n, k) > content_chunking.MAX_COMBINATIONS_TO_EVALUATE:
                continue  # random sampling path: depends on Python's RNG
            items = [f'i{j}' for j in range(n)]
            out = call(content_chunking.generate_covering_chunks, items, k)
            case = {'input': {'items': items, 'k': k},
                    'output': out if isinstance(out, dict) else [[c, idx] for c, idx in out]}
            if k < 2 < n:
                # No chunk of one item covers a pair, so every pair goes through upstream's fallback, which appends
                # them in frozenset iteration order. The port's order differs (documented); compare as a set.
                case['unordered'] = True
            covering.append(case)

    write(
        'content_chunking',
        [
            'graphiti_core.utils.content_chunking.estimate_tokens',
            'should_chunk',
            'chunk_json_content',
            'chunk_text_content',
            'chunk_message_content',
            'generate_covering_chunks',
        ],
        {
            'estimate_tokens': estimate,
            'should_chunk': should,
            'chunk_json_content': json_chunks,
            'chunk_text_content': text_chunks,
            'chunk_message_content': message_chunks,
            'generate_covering_chunks': covering,
        },
    )


# ---------------------------------------------------------------------------------------------------------------
# community_operations.py


class _Timeout(Exception):
    pass


def _on_alarm(signum, frame):
    raise _Timeout()


def gen_community_operations() -> None:
    rng = random.Random(10010)
    Neighbor = community_operations.Neighbor
    cases = []
    skipped = 0
    signal.signal(signal.SIGALRM, _on_alarm)
    while len(cases) < 120:
        n = rng.randint(0, 10)
        uuids = [f'n{i}' for i in range(n)]
        rng.shuffle(uuids)
        counts: dict[tuple[str, str], int] = {}
        for _ in range(rng.randint(0, n * 2)):
            a, b = rng.choice(uuids), rng.choice(uuids)
            if a == b and rng.random() < 0.7:
                continue
            key = (min(a, b), max(a, b))
            counts[key] = counts.get(key, 0) + rng.choice([1, 1, 2, 3])
        projection = {u: [] for u in uuids}
        for (a, b), c in counts.items():
            projection[a].append(Neighbor(node_uuid=b, edge_count=c))
            if a != b:
                projection[b].append(Neighbor(node_uuid=a, edge_count=c))
        signal.setitimer(signal.ITIMER_REAL, 0.1)
        try:
            clusters = community_operations.label_propagation(projection)
        except _Timeout:
            skipped += 1  # upstream oscillates forever on this graph
            continue
        finally:
            signal.setitimer(signal.ITIMER_REAL, 0)
        cases.append({
            'input': [[u, [[nb.node_uuid, nb.edge_count] for nb in nbs]] for u, nbs in projection.items()],
            'output': clusters,
        })

    write('community_operations', ['graphiti_core.utils.maintenance.community_operations.label_propagation'],
          {'label_propagation': cases})
    print(f'  label_propagation: skipped {skipped} graphs on which upstream never converges')


def main() -> None:
    logging.disable(logging.CRITICAL)
    from importlib.metadata import version

    installed = version('graphiti-core')
    if installed != UPSTREAM_VERSION:
        sys.exit(f'graphiti-core {installed} is installed; the fixtures need {UPSTREAM_VERSION}')
    gen_search_utils()
    gen_dedup_helpers()
    gen_edge_operations()
    gen_temporal_edge_utils()
    gen_text_utils()
    gen_bulk_utils()
    gen_attribute_utils()
    gen_prompt_helpers()
    gen_content_chunking()
    gen_community_operations()


if __name__ == '__main__':
    main()
