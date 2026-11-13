# GraphitiNet

Temporal knowledge graphs for AI agents, in .NET. graphiti-net is a C# rewrite of
[Graphiti](https://github.com/getzep/graphiti) v0.31.2, the open-source Python library from Zep. It turns chat
messages, documents and JSON into a knowledge graph an agent can search. Each fact records when it was true and which
episode it came from.

This package holds the library: ingestion, hybrid search, the OpenAI and Microsoft.Extensions.AI providers, and an
in-memory graph driver. For production storage, add
[GraphitiNet.PostgreSql](https://www.nuget.org/packages/GraphitiNet.PostgreSql).

**Preview.** The API follows Python Graphiti's and may still change before 1.0.

> Graphiti is a brand of Zep Software, Inc. graphiti-net is an independent, community-built rewrite. It is not
> affiliated with, endorsed by, or sponsored by Zep Software, Inc.

## Install

```bash
dotnet add package GraphitiNet --prerelease
```

## Quickstart

Set `OPENAI_API_KEY`, then:

```csharp
using GraphitiNet;
using GraphitiNet.Driver.InMemory;
using GraphitiNet.Llm;
using GraphitiNet.Models;
using GraphitiNet.Search;

await using var graphiti = new Graphiti(
    new InMemoryGraphDriver(),
    new OpenAIClient(new LLMConfig(model: "gpt-5-mini", smallModel: "gpt-5-mini")));
await graphiti.BuildIndicesAndConstraintsAsync();

await graphiti.AddEpisodeAsync(
    name: "Freakonomics Radio 0",
    episodeBody: "Kamala Harris is the Attorney General of California. " +
                 "She was previously the district attorney for San Francisco.",
    sourceDescription: "podcast transcript",
    referenceTime: DateTimeOffset.UtcNow,
    source: EpisodeType.Text);

// Facts: hybrid search (similarity and keywords, fused with RRF).
foreach (var fact in await graphiti.SearchAsync("Who was the California Attorney General?"))
{
    Console.WriteLine(fact.Fact);
}

// Entities: a search recipe.
var nodes = await graphiti.SearchAsync("California", SearchConfigRecipes.NodeHybridSearchRrf with { Limit = 5 });
foreach (var node in nodes.Nodes)
{
    Console.WriteLine($"{node.Name}: {node.Summary}");
}
```

Any `Microsoft.Extensions.AI` `IChatClient` and `IEmbeddingGenerator` work too, through `ChatClientLLMClient` and
`EmbeddingGeneratorEmbedder`.

## Learn more

- [README and status](https://github.com/tekthar/graphiti-net#readme)
- [API compatibility with Python Graphiti](https://github.com/tekthar/graphiti-net/blob/main/docs/api-compatibility.md)
- [Quickstart sample](https://github.com/tekthar/graphiti-net/tree/main/samples/Quickstart)

## Licence

Apache-2.0. Parts are translated from Graphiti's Python source, prompts and tests; see `NOTICE` and
`THIRD-PARTY-NOTICES.md` in this package. "Graphiti" and "Zep" are trademarks of Zep Software, Inc.
