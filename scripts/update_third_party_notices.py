# Copyright 2026, graphiti-net contributors
# Licensed under the Apache License, Version 2.0. See LICENSE.
"""Rebuild the translated-files table in THIRD-PARTY-NOTICES.md from the "Ported from" headers in src/ and tests/.

Run from the repository root: python3 scripts/update_third_party_notices.py
"""

import pathlib
import re

ROOT = pathlib.Path(__file__).resolve().parent.parent
HEADER = re.compile(r'// Ported from (.+?) \(Graphiti v[0-9.]+\)', re.S)
EXTRA_ROWS = [
    '| `tests/GraphitiNet.Core.Tests/Prompts/Fixtures/prompt_renders.json`, `response_schemas.json` '
    '| Prompt text and schemas generated from `graphiti_core/prompts/*.py` |',
    '| `tests/GraphitiNet.Core.Tests/Prompts/Fixtures/prompt_contexts.json` '
    '| The `upstream_test` context from `tests/prompts/test_legacy_prompt_compatibility.py` |',
]


def sources(text: str) -> str:
    joined = re.sub(r'\s*\n//\s*', ' ', text)
    match = HEADER.search(joined)
    if not match:
        return ''
    parts = re.split(r', (?![^()]*\))| and ', re.sub(r'\s+', ' ', match.group(1).strip()))
    return ', '.join(f'`{p.strip()}`' for p in parts if p.strip())


def main() -> None:
    rows = []
    for path in sorted([*ROOT.glob('src/**/*.cs'), *ROOT.glob('tests/**/*.cs')]):
        if '/obj/' in path.as_posix() or '/bin/' in path.as_posix():
            continue
        found = sources(path.read_text(encoding='utf-8')[:2000])
        if found:
            rows.append(f'| `{path.relative_to(ROOT).as_posix()}` | {found} |')
    table = '| graphiti-net file | Upstream file |\n| --- | --- |\n' + '\n'.join(rows + EXTRA_ROWS) + '\n'
    notices = ROOT / 'THIRD-PARTY-NOTICES.md'
    text = notices.read_text(encoding='utf-8')
    text = re.sub(r'\| graphiti-net file \| Upstream file \|\n\| --- \| --- \|\n(?:\|.*\n)+', table, text)
    notices.write_text(text, encoding='utf-8')
    print(f'{len(rows)} translated files listed')


if __name__ == '__main__':
    main()
