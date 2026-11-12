# Development environment

What you need to build the port and run parity tests against Python Graphiti.

## Services

| Service | Used for |
| --- | --- |
| .NET 10 SDK | Building and testing the port |
| Neo4j 5.26 | The main graph driver (`docker run neo4j:5.26`) |
| PostgreSQL 16 with pgvector | The EF Core backend |
| Python with `graphiti-core` | The reference for parity runs. Install the pinned baseline release, not the latest |
| An LLM and an embedding provider | Ingestion and search, for both the port and Python Graphiti |

## Settings

Both the port and Python Graphiti read the same settings, so a parity run compares like with like.

| Variable | Example value | Purpose |
| --- | --- | --- |
| `OPENAI_API_KEY` | (your key) | LLM and embeddings for both Graphiti and the port |
| `ANTHROPIC_API_KEY` | (your key) | Claude as the test LLM, if used |
| `NEO4J_URI` | `bolt://localhost:7687` | Graph database |
| `NEO4J_USER` / `NEO4J_PASSWORD` | `neo4j` / a dev-only password | Graph database login (local container only) |
| `ConnectionStrings__Graph` | `Host=localhost;Database=graphiti;Username=postgres` | EF Core backend |
| `LLM_MODEL` / `EMBEDDING_MODEL` / `EMBEDDING_DIM` | `gpt-5-mini` / `text-embedding-3-small` / `1536` | Pinned models, identical for Python and .NET |
| `SEMAPHORE_LIMIT` | `10` | LLM concurrency, shared by both versions |
| `GRAPHITI_TELEMETRY_ENABLED` | `false` | Turn off Python Graphiti telemetry |
| `DOTNET_CLI_TELEMETRY_OPTOUT` / `DOTNET_NOLOGO` | `1` / `1` | Quieter, telemetry-free .NET CLI |

Never commit keys. Keep them in environment variables or `dotnet user-secrets`.

Run benchmarks with hosted models: a local Ollama model on a CPU-only machine is too slow.

## Claude Code cloud environment

To work on the port in Claude Code on the web, create one custom cloud environment for the project. Each session gets
a fresh Ubuntu 24.04 VM with about 4 vCPUs, 16 GB RAM and 30 GB disk. Docker, PostgreSQL 16 and Python come
preinstalled, but the .NET SDK does not, so the setup script installs it.

**Network access:** Custom, with "Also include default list of common package managers" ticked. That default list
already covers NuGet, dot.net, Docker Hub and PyPI. Add only the LLM hosts you use, for example `api.openai.com` and
`api.voyageai.com`.

**Setup script** (runs as root, must finish in about 5 minutes to be cached; cached for about 7 days):

```bash
#!/bin/bash
set -e
# .NET SDK (not preinstalled)
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet
ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
# pgvector for the EF Core backend
apt-get update && apt-get install -y postgresql-16-pgvector || true
# Python Graphiti, the reference for parity runs (pin to the baseline release once chosen)
pip install graphiti-core || true
# Pre-pull Neo4j so it is cached on disk
docker pull neo4j:5.26 || true
```

**Services start per session:** the cache keeps files, not running processes. Add a SessionStart hook in the repo's
`.claude/settings.json` that starts Neo4j (`docker run`) and PostgreSQL (`service postgresql start`), and exits early
unless `CLAUDE_CODE_REMOTE` is `true`.

**Where to keep keys:** everyone who uses the environment can read its environment variables, so keep the environment
personal and store every key that allows it as a network secret:

- `OPENAI_API_KEY`: a network secret on `api.openai.com`. Keep a placeholder in the environment variables if the SDK
  insists on one.
- `ANTHROPIC_API_KEY`: an environment variable, because network secrets never attach to `api.anthropic.com`.
- Everything else in the settings table above: environment variables, plus `BASH_DEFAULT_TIMEOUT_MS=600000` so long
  test and benchmark runs can finish.

See [Configure cloud environments](https://code.claude.com/docs/en/cloud-environments.md) in the Claude Code docs.
