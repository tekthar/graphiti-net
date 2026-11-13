// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

// Question answering on LongMemEval with graphiti-net, the same way parity/python/lme_qa.py runs it with Python
// Graphiti: ingest each selected question's haystack message by message, search the question's group with the
// combined hybrid RRF recipe (facts and entities, 20 each), answer with the port of upstream's eval.qa_prompt, and
// write the answers in lme_qa.py's file shape, so `lme_qa.py grade` grades both implementations identically.
//
// Usage:
//   qa <longmemeval_oracle.json> <questions.json> <answers.json> [label]
//   qa-answer <answers.json> <out.json>     answers again from the stored contexts (no ingestion, no search)
// Settings: as the build command, plus QA_CONCURRENCY (default 8). GRAPHITI_EVAL_POSTGRES selects PostgreSQL
// (schema lme_qa); the in-memory driver is used when it is not set.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GraphitiNet;
using GraphitiNet.CrossEncoder;
using GraphitiNet.Driver;
using GraphitiNet.Driver.InMemory;
using GraphitiNet.Driver.PostgreSql;
using GraphitiNet.Embedder;
using GraphitiNet.Llm;
using GraphitiNet.Models;
using GraphitiNet.Prompts;
using GraphitiNet.Search;

internal static class Qa
{
    private const int SearchLimit = 20;

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static LLMConfig LlmConfig()
    {
        var model = Environment.GetEnvironmentVariable("LLM_MODEL") ?? "gpt-5-mini";
        return new LLMConfig(model: model, smallModel: Environment.GetEnvironmentVariable("SMALL_LLM_MODEL") ?? model);
    }

    private static int Concurrency() =>
        int.Parse(Environment.GetEnvironmentVariable("QA_CONCURRENCY") ?? "8", CultureInfo.InvariantCulture);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is < 3 or > 4)
        {
            Console.Error.WriteLine("usage: qa <longmemeval_oracle.json> <questions.json> <answers.json> [label]");
            return 2;
        }

        var label = args.Length == 4 ? args[3] : "net";
        using var data = JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
        var byId = data.RootElement.EnumerateArray().ToDictionary(q => q.GetProperty("question_id").GetString()!);
        var ids = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(args[1]))!;

        var embedderConfig = new OpenAIEmbedderConfig
        {
            EmbeddingModel = Environment.GetEnvironmentVariable("EMBEDDING_MODEL") ?? "text-embedding-3-small",
            EmbeddingDim = int.Parse(
                Environment.GetEnvironmentVariable("EMBEDDING_DIM") ?? "1024", CultureInfo.InvariantCulture),
        };
        var postgres = Environment.GetEnvironmentVariable("GRAPHITI_EVAL_POSTGRES") is { Length: > 0 } connection
            ? connection
            : null;
        IGraphDriver driver = postgres is not null
            ? new PostgresDriver(postgres, new PostgresGraphOptions { Schema = "lme_qa", EmbeddingDimension = embedderConfig.EmbeddingDim })
            : new InMemoryGraphDriver();
        await using var setup = new Graphiti(driver, new OpenAIClient(LlmConfig()), new OpenAIEmbedder(embedderConfig));
        await setup.BuildIndicesAndConstraintsAsync(deleteExisting: true);

        var config = SearchConfigRecipes.CombinedHybridSearchRrf with
        {
            EpisodeConfig = null,
            CommunityConfig = null,
            Limit = SearchLimit,
        };
        using var gate = new SemaphoreSlim(Concurrency());
        var started = Stopwatch.StartNew();

        // Each finished question is appended to a checkpoint file, so a run stopped part way (quota, crash) resumes with
        // the questions it has not answered yet.
        var checkpoint = args[2] + ".partial.jsonl";
        var done = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (File.Exists(checkpoint))
        {
            foreach (var line in await File.ReadAllLinesAsync(checkpoint))
            {
                if (line.Length > 0 && JsonNode.Parse(line) is JsonObject row)
                {
                    done[row["question_id"]!.GetValue<string>()] = row;
                }
            }

            Console.WriteLine($"resuming: {done.Count} questions already answered");
        }

        using var checkpointLock = new SemaphoreSlim(1);

        async Task<JsonObject> OneAsync(string id)
        {
            if (done.TryGetValue(id, out var finished))
            {
                return finished;
            }

            var answered = await AttemptsAsync(id);
            await checkpointLock.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(checkpoint, answered.ToJsonString(Compact) + "\n");
            }
            finally
            {
                checkpointLock.Release();
            }

            return answered;
        }

        // A transient provider error (rate limit, 502) fails the whole AddEpisodeAsync, as in Graphiti itself; retry the
        // question from scratch in a fresh group rather than lose the run.
        async Task<JsonObject> AttemptsAsync(string id)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await AttemptAsync(id, attempt);
                }
                catch (Exception e) when (attempt < 2)
                {
                    Console.WriteLine($"{id}: attempt {attempt + 1} failed ({e.GetType().Name}); retrying");
                    await Task.Delay(TimeSpan.FromSeconds(30));
                }
            }
        }

        async Task<JsonObject> AttemptAsync(string id, int attempt)
        {
            await gate.WaitAsync();
            try
            {
                var item = byId[id];
                // One client per question, so each question's token usage is its own. The Graphiti instances share
                // the driver and are not disposed (that would close it); `setup` disposes it at the end.
                var llm = new OpenAIClient(LlmConfig());
                var graphiti = new Graphiti(
                    driver, llm, new OpenAIEmbedder(embedderConfig), new OpenAIRerankerClient(LlmConfig()));
                var groupId = $"lme_qa_{label}_{id}" + (attempt > 0 ? $"_r{attempt}" : "");
                var sessions = item.GetProperty("haystack_sessions");
                var dates = item.GetProperty("haystack_dates");
                var messages = 0;
                var watch = Stopwatch.StartNew();
                for (var s = 0; s < sessions.GetArrayLength(); s++)
                {
                    var date = DateTimeOffset.ParseExact(
                        dates[s].GetString()! + " +00:00", "yyyy/MM/dd (ddd) HH:mm zzz", CultureInfo.InvariantCulture);
                    foreach (var message in sessions[s].EnumerateArray())
                    {
                        messages++;
                        await graphiti.AddEpisodeAsync(
                            name: "",
                            episodeBody: $"{message.GetProperty("role").GetString()}: {message.GetProperty("content").GetString()}",
                            sourceDescription: "",
                            referenceTime: date,
                            source: EpisodeType.Message,
                            groupId: groupId);
                    }
                }

                var ingestSeconds = watch.Elapsed.TotalSeconds;
                var ingestUsage = llm.TokenTracker.GetTotalUsage();

                watch.Restart();
                var results = await graphiti.SearchAsync(item.GetProperty("question").GetString()!, config, [groupId]);
                var searchMs = watch.Elapsed.TotalMilliseconds;

                var facts = new JsonArray([.. results.Edges.Select(e => (JsonNode)new JsonObject
                {
                    ["fact"] = e.Fact,
                    ["valid_at"] = e.ValidAt is { } v ? PythonStr(v) : "None",
                    ["invalid_at"] = e.InvalidAt is { } i ? PythonStr(i) : "Present",
                })]);
                var entities = new JsonArray([.. results.Nodes.Select(n => (JsonNode)new JsonObject
                {
                    ["entity_name"] = n.Name,
                    ["summary"] = n.Summary,
                })]);
                var question = item.GetProperty("question").GetString()!;
                var answer = new JsonObject
                {
                    ["question_id"] = id,
                    ["question_type"] = item.GetProperty("question_type").GetString(),
                    ["question"] = question,
                    ["question_date"] = item.GetProperty("question_date").GetString(),
                    ["gold_answer"] = JsonNode.Parse(item.GetProperty("answer").GetRawText()),
                    ["messages"] = messages,
                    ["ingest_seconds"] = Math.Round(ingestSeconds, 1),
                    ["ingest_input_tokens"] = ingestUsage.InputTokens,
                    ["ingest_output_tokens"] = ingestUsage.OutputTokens,
                    ["search_ms"] = Math.Round(searchMs, 1),
                    ["facts"] = facts,
                    ["entity_summaries"] = entities,
                };
                var query = $"{question} (asked on {item.GetProperty("question_date").GetString()})";
                await AnswerAsync(llm, answer, query);
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture, $"{id}: {messages} messages in {ingestSeconds:F0}s"));
                return answer;
            }
            finally
            {
                gate.Release();
            }
        }

        var answers = await Task.WhenAll(ids.Select(OneAsync));
        var output = new JsonObject
        {
            ["implementation"] = postgres is not null ? "graphiti-net (PostgreSQL)" : "graphiti-net (in memory)",
            ["label"] = label,
            ["answers"] = new JsonArray([.. answers]),
        };
        await File.WriteAllTextAsync(args[2], output.ToJsonString(Indented));
        File.Delete(checkpoint);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture, $"{answers.Length} questions in {started.Elapsed.TotalSeconds:F0}s -> {args[2]}"));
        return 0;
    }

    public static async Task<int> AnswerAgainAsync(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: qa-answer <answers.json> <out.json>");
            return 2;
        }

        var run = JsonNode.Parse(await File.ReadAllTextAsync(args[0]))!.AsObject();
        using var gate = new SemaphoreSlim(Concurrency());
        await Task.WhenAll(run["answers"]!.AsArray().Select(async node =>
        {
            await gate.WaitAsync();
            try
            {
                await AnswerAsync(new OpenAIClient(LlmConfig()), node!.AsObject(), node["qa_query"]!.GetValue<string>());
            }
            finally
            {
                gate.Release();
            }
        }));
        run["label"] = run["label"]?.GetValue<string>() + "+reanswer";
        await File.WriteAllTextAsync(args[1], run.ToJsonString(Indented));
        return 0;
    }

    /// <summary>Answers with eval.qa_prompt from the facts and entity summaries in <paramref name="answer"/>.</summary>
    private static async Task AnswerAsync(OpenAIClient llm, JsonObject answer, string query)
    {
        var context = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["entity_summaries"] = ToPlain(answer["entity_summaries"]!.AsArray()),
            ["facts"] = ToPlain(answer["facts"]!.AsArray()),
        };
        // AsMessages appends the DO_NOT_ESCAPE_UNICODE note, as upstream's prompt_library does.
        var messages = PromptLibrary.Default.Build(PromptNames.EvalQaPrompt, context).AsMessages().ToList();
        var system = messages[0].Content;
        var user = messages[1].Content;
        var before = llm.TokenTracker.GetTotalUsage();
        var response = await llm.GenerateResponseAsync<QAResponse>(messages, promptName: "eval.qa_prompt");
        var after = llm.TokenTracker.GetTotalUsage();
        answer["qa_query"] = query;
        answer["qa_prompt_system"] = system;
        answer["qa_prompt_user"] = user;
        answer["response"] = response.Answer;
        answer["answer_input_tokens"] = after.InputTokens - before.InputTokens;
        answer["answer_output_tokens"] = after.OutputTokens - before.OutputTokens;
    }

    /// <summary>A list of string-valued objects, in insertion order, as the prompt JSON serializer takes them.</summary>
    private static List<Dictionary<string, object?>> ToPlain(JsonArray items) =>
        [.. items.Select(item => item!.AsObject().ToDictionary(p => p.Key, p => (object?)p.Value?.GetValue<string>()))];

    /// <summary>Python's <c>str()</c> of an aware datetime, in UTC: <c>2024-01-02 03:04:05[.ffffff]+00:00</c>.</summary>
    private static string PythonStr(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        var builder = new StringBuilder(utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        var microseconds = (utc.Ticks % TimeSpan.TicksPerSecond) / 10;
        if (microseconds != 0)
        {
            builder.Append('.').Append(microseconds.ToString("D6", CultureInfo.InvariantCulture));
        }

        return builder.Append("+00:00").ToString();
    }
}
