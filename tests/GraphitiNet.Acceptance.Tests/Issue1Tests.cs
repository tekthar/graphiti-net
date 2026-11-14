// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Search;
using static GraphitiNet.Acceptance.Tests.Support;

namespace GraphitiNet.Acceptance.Tests;

/// <summary>
/// AbpAgent's requirements, tekthar/graphiti-net#1. Each test is named after a criterion, runs through the public
/// <see cref="Graphiti"/> API with a scripted model, and runs in memory, on PostgreSQL and on SQL Server.
/// </summary>
public class Issue1Tests
{
    // ---- Done when: filtering ---------------------------------------------------------------------------------

    private static readonly ScriptedEpisode AliceAcme = new(
        "Alice works at Acme.",
        ["Alice", "Acme"],
        [new("Alice", "WORKS_AT", "Acme", "Alice works at Acme.")]);

    private static readonly ScriptedEpisode AliceTennis = new(
        "Alice plays tennis at the Riverside Club.",
        ["Alice", "Riverside Club"],
        [new("Alice", "PLAYS_AT", "Riverside Club", "Alice plays tennis at the Riverside Club.")]);

    private static readonly ScriptedEpisode BobGlobex = new(
        "Bob works at Globex.",
        ["Bob", "Globex"],
        [new("Bob", "WORKS_AT", "Globex", "Bob works at Globex.")]);

    [SkippableTheory]
    [MemberData(nameof(Drivers.All), MemberType = typeof(Drivers))]
    public async Task Episodes_in_two_groups_with_one_filtered_out_return_neither_its_fact_nor_a_summary_built_from_it(
        string driver)
    {
        await using var h = await Harness.CreateAsync(driver, new ScriptedLlm(AliceAcme, AliceTennis, BobGlobex));
        var groupA = Harness.NewGroup();
        var groupB = Harness.NewGroup();
        await h.AddAsync(groupA, AliceAcme.Body, Jan);
        var hidden = (await h.AddAsync(groupA, AliceTennis.Body, Mar)).Episode;
        await h.AddAsync(groupB, BobGlobex.Body, Jan);
        const string query = "Alice tennis Riverside Club Acme Bob works Globex";

        // Without the filter the hidden episode's fact and the summary built from it are found (the control).
        var open = await h.Graphiti.SearchAsync(query, Everything, [groupA, groupB]);
        Assert.Contains(open.Edges, e => e.Fact == "Alice plays tennis at the Riverside Club.");
        Assert.Contains(open.Nodes, n => n.Summary.Contains("tennis", StringComparison.Ordinal));

        var filtered = await h.Graphiti.SearchAsync(query, Everything, [groupA, groupB],
            episodeAccessFilter: new HideEpisodes(hidden.Uuid));

        Assert.DoesNotContain(filtered.Edges, e => e.Episodes.Contains(hidden.Uuid));
        Assert.DoesNotContain(filtered.Edges, e => e.Fact.Contains("tennis", StringComparison.Ordinal));
        Assert.DoesNotContain(filtered.Nodes, n => n.Summary.Contains("tennis", StringComparison.Ordinal)
                                                   || n.Name == "Riverside Club");
        Assert.DoesNotContain(filtered.Episodes, e => e.Uuid == hidden.Uuid);
        Assert.Empty(filtered.Communities);

        // What the caller may read is still there, from both groups.
        Assert.Contains(filtered.Edges, e => e.Fact == "Alice works at Acme.");
        Assert.Contains(filtered.Edges, e => e.Fact == "Bob works at Globex.");
    }

    // ---- Done when: strict removal ----------------------------------------------------------------------------

    private static readonly ScriptedEpisode AliceWorks = new(
        "Alice works at Acme.",
        ["Alice", "Acme"],
        [new("Alice", "WORKS_AT", "Acme", "Alice works at Acme.")]);

    private static readonly ScriptedEpisode Removed = new(
        "Alice plays tennis with Carol at the Riverside Club.",
        ["Alice", "Carol", "Riverside Club"],
        [
            new("Alice", "PLAYS_TENNIS_WITH", "Carol", "Alice plays tennis with Carol."),
            new("Alice", "MEMBER_OF", "Riverside Club", "Alice is a member of the Riverside Club."),
        ]);

    // Restates a fact of the removed episode: the fact then lists both episodes, and its text is the removed
    // episode's.
    private static readonly ScriptedEpisode Restates = new(
        "Carol and Alice meet every Sunday.",
        ["Carol", "Alice"],
        [new("Alice", "PLAYS_TENNIS_WITH", "Carol", "Alice and Carol meet on Sundays.",
            DuplicateOf: "Alice plays tennis with Carol.")]);

    [SkippableTheory]
    [MemberData(nameof(Drivers.All), MemberType = typeof(Drivers))]
    public async Task After_a_strict_removal_no_text_from_the_removed_episode_is_left_in_any_fact_or_summary(
        string driver)
    {
        await using var h = await Harness.CreateAsync(driver, new ScriptedLlm(AliceWorks, Removed, Restates));
        var group = Harness.NewGroup();
        await h.AddAsync(group, AliceWorks.Body, Jan);
        var removed = (await h.AddAsync(group, Removed.Body, Mar)).Episode;
        var restated = await h.AddAsync(group, Restates.Body, Jun);

        // Before: the removed episode's text is in facts (one also listing the later episode) and in summaries.
        var shared = Assert.Single(await h.FactsAsync(group), e => e.Fact == "Alice plays tennis with Carol.");
        Assert.Equal([removed.Uuid, restated.Episode.Uuid], shared.Episodes);
        Assert.Contains(await h.EntitiesAsync(group), n => n.Name == "Carol" && n.Summary.Contains("tennis", StringComparison.Ordinal));
        Assert.Contains(await h.EntitiesAsync(group), n => n.Name == "Alice" && n.Summary.Contains("Riverside", StringComparison.Ordinal));

        await h.Graphiti.RemoveEpisodeAsync(removed.Uuid, strict: true);

        string[] removedText = ["Riverside", "tennis", .. Removed.Facts.Select(f => f.Fact)];
        var facts = await h.FactsAsync(group);
        var entities = await h.EntitiesAsync(group);
        foreach (var text in removedText)
        {
            Assert.DoesNotContain(facts, e => e.Fact.Contains(text, StringComparison.Ordinal));
            Assert.DoesNotContain(entities, n => n.Summary.Contains(text, StringComparison.Ordinal)
                                                 || n.Name.Contains(text, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(facts, e => e.Episodes.Contains(removed.Uuid));
        Assert.DoesNotContain(await h.EpisodesAsync(group), e => e.Uuid == removed.Uuid);

        // What other episodes say stays: their facts, and summaries rebuilt from them.
        Assert.Contains(facts, e => e.Fact == "Alice works at Acme.");
        Assert.Equal("Alice works at Acme. Carol and Alice meet every Sunday.",
            Assert.Single(entities, n => n.Name == "Alice").Summary);
        Assert.Equal("Carol and Alice meet every Sunday.", Assert.Single(entities, n => n.Name == "Carol").Summary);
    }

    // ---- Done when: two concurrent adds -----------------------------------------------------------------------

    private static readonly ScriptedEpisode AliceChess = new(
        "Alice joined the chess club.",
        ["Alice", "chess club"],
        [new("Alice", "MEMBER_OF", "chess club", "Alice is a member of the chess club.")]);

    private static readonly ScriptedEpisode AliceBike = new(
        "Alice bought a new bike.",
        ["Alice", "bike"],
        [new("Alice", "OWNS", "bike", "Alice owns a new bike.")]);

    /// <summary>Holds every entity-extraction call until released, recording who got in.</summary>
    private sealed class ExtractionBlocker
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entries;

        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _bothResolved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _edgeEntries;

        /// <summary>
        /// Also holds each add at fact extraction until both have got there, that is until both have resolved their
        /// entities. Without the group lock this guarantees neither add saves before the other looks for duplicates;
        /// with the lock it would deadlock, so only the no-lock control sets it.
        /// </summary>
        public bool HoldUntilBothResolvedEntities { get; init; }

        public void Release() => _release.TrySetResult();

        public async Task OnAnswerAsync(string prompt, CancellationToken cancellationToken)
        {
            if (HoldUntilBothResolvedEntities && prompt == "extract_edges.edge")
            {
                if (Interlocked.Increment(ref _edgeEntries) >= 2)
                {
                    _bothResolved.TrySetResult();
                }

                await _bothResolved.Task.WaitAsync(WaitLimit, cancellationToken);
                return;
            }

            if (prompt != "extract_nodes.extract_message")
            {
                return;
            }

            var entry = Interlocked.Increment(ref _entries);
            (entry == 1 ? FirstEntered : SecondEntered).TrySetResult();
            await _release.Task.WaitAsync(WaitLimit, cancellationToken);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Drivers.All), MemberType = typeof(Drivers))]
    public async Task Two_concurrent_adds_to_one_group_make_one_entity(string driver)
    {
        var llm = new ScriptedLlm(AliceChess, AliceBike);
        var blocker = new ExtractionBlocker();
        llm.BeforeAnswer = blocker.OnAnswerAsync;
        await using var h = await Harness.CreateAsync(driver, llm); // the default group lock
        var group = Harness.NewGroup();

        var first = h.AddAsync(group, AliceChess.Body, Jan);
        await blocker.FirstEntered.Task.WaitAsync(WaitLimit);
        var second = h.AddAsync(group, AliceBike.Body, Jan);

        // Both calls are in flight; the second waits for the group while the first is inside a model call.
        await Task.Delay(300);
        Assert.False(second.IsCompleted);
        Assert.False(blocker.SecondEntered.Task.IsCompleted);

        blocker.Release();
        var results = await Task.WhenAll(first, second).WaitAsync(WaitLimit);

        var alice = Assert.Single(await h.EntitiesAsync(group), n => n.Name == "Alice");
        Assert.All(results, r => Assert.Contains(r.Nodes, n => n.Uuid == alice.Uuid));
        Assert.All(await h.FactsAsync(group), e => Assert.Equal(alice.Uuid, e.SourceNodeUuid));
    }

    [SkippableTheory]
    [MemberData(nameof(Drivers.All), MemberType = typeof(Drivers))]
    public async Task Control_without_the_group_lock_two_concurrent_adds_can_make_two_entities(string driver)
    {
        var llm = new ScriptedLlm(AliceChess, AliceBike);
        var blocker = new ExtractionBlocker { HoldUntilBothResolvedEntities = true };
        llm.BeforeAnswer = blocker.OnAnswerAsync;
        await using var h = await Harness.CreateAsync(driver, llm, groupLock: NoGroupLock.Instance);
        var group = Harness.NewGroup();

        var first = h.AddAsync(group, AliceChess.Body, Jan);
        await blocker.FirstEntered.Task.WaitAsync(WaitLimit);
        var second = h.AddAsync(group, AliceBike.Body, Jan);
        await blocker.SecondEntered.Task.WaitAsync(WaitLimit); // both are inside extraction at once

        blocker.Release();
        await Task.WhenAll(first, second).WaitAsync(WaitLimit);

        Assert.Equal(2, (await h.EntitiesAsync(group)).Count(n => n.Name == "Alice"));
    }

    // ---- Done when: call context and token usage --------------------------------------------------------------

    [SkippableTheory]
    [MemberData(nameof(Drivers.All), MemberType = typeof(Drivers))]
    public async Task A_model_call_receives_the_episodes_group_id_and_metadata_and_add_episode_reports_its_tokens(
        string driver)
    {
        await using var h = await Harness.CreateAsync(driver, new ScriptedLlm(AliceAcme, BobGlobex));
        var group = Harness.NewGroup();
        var metadata = new Dictionary<string, object?> { ["tenant"] = "t-7", ["conversation"] = "c-42" };

        await h.AddAsync(group, BobGlobex.Body, Jan); // an earlier call, whose tokens must not be counted below
        var callsBefore = h.Llm.Calls.Count;
        var embeddingsBefore = h.Embedder.Contexts.Count;
        var result = await h.AddAsync(group, AliceAcme.Body, Mar, metadata);

        var calls = h.Llm.Calls.Skip(callsBefore).ToList();
        Assert.Contains(calls, c => c.PromptName == "extract_nodes.extract_message");
        Assert.Contains(calls, c => c.PromptName == "extract_edges.edge");
        Assert.All(calls, call =>
        {
            Assert.NotNull(call.Context);
            Assert.Equal("add_episode", call.Context.Operation);
            Assert.Equal(group, call.Context.GroupId);
            Assert.Equal(result.Episode.Uuid, call.Context.EpisodeUuid);
            Assert.Equal("t-7", call.Context.EpisodeMetadata?["tenant"]);
            Assert.Equal("c-42", call.Context.EpisodeMetadata?["conversation"]);
            Assert.Equal(call.PromptName, call.Context.PromptName);
        });
        var embeddings = h.Embedder.Contexts.Skip(embeddingsBefore).ToList();
        Assert.NotEmpty(embeddings);
        Assert.All(embeddings, context => Assert.Equal(group, context?.GroupId));

        Assert.Equal(
            new Llm.TokenUsage(calls.Count * ScriptedLlm.InputTokensPerCall, calls.Count * ScriptedLlm.OutputTokensPerCall),
            result.TokenUsage);
        Assert.Equal(calls.Count, result.TokenUsageByPrompt.Values.Sum(p => p.CallCount));
        Assert.Equal(1, result.TokenUsageByPrompt["extract_nodes.extract_message"].CallCount);
    }

    // ---- Done when: Arabic keyword search ---------------------------------------------------------------------

    private static readonly ScriptedEpisode Arabic = new(
        "تعمل سارة في شركة النور للبرمجيات في القاهرة.",
        ["سارة", "شركة النور", "القاهرة"],
        [
            new("سارة", "WORKS_AT", "شركة النور", "سارة تعمل في شركة النور."),
            new("شركة النور", "LOCATED_IN", "القاهرة", "مقر شركة النور في القاهرة."),
        ]);

    [SkippableTheory]
    [MemberData(nameof(Drivers.All), MemberType = typeof(Drivers))]
    public async Task An_Arabic_episode_can_be_found_by_keyword_search(string driver)
    {
        var group = Harness.NewGroup();
        await using var h = await Harness.CreateAsync(driver, new ScriptedLlm(Arabic),
            textSearchConfigs: new Dictionary<string, string> { [group] = "arabic" });
        var episode = (await h.AddAsync(group, Arabic.Body, Jan)).Episode;

        var results = await h.Graphiti.SearchAsync("شركة النور", KeywordOnly, [group]);

        Assert.Contains(results.Edges, e => e.Fact == "سارة تعمل في شركة النور.");
        Assert.Contains(results.Nodes, n => n.Name == "شركة النور");
        Assert.Contains(results.Episodes, e => e.Uuid == episode.Uuid);
    }

    // ---- Also from #1 -----------------------------------------------------------------------------------------

    [SkippableTheory]
    [MemberData(nameof(Drivers.All), MemberType = typeof(Drivers))]
    public async Task Entities_never_merge_across_groups(string driver)
    {
        await using var h = await Harness.CreateAsync(driver, new ScriptedLlm(AliceAcme));
        var groupA = Harness.NewGroup();
        var groupB = Harness.NewGroup();

        var a = await h.AddAsync(groupA, AliceAcme.Body, Jan);
        var b = await h.AddAsync(groupB, AliceAcme.Body, Jan); // the same text, in another group

        var inA = await h.EntitiesAsync(groupA);
        var inB = await h.EntitiesAsync(groupB);
        Assert.Equal(["Acme", "Alice"], inA.Select(n => n.Name).Order());
        Assert.Equal(["Acme", "Alice"], inB.Select(n => n.Name).Order());
        Assert.Empty(inA.Select(n => n.Uuid).Intersect(inB.Select(n => n.Uuid)));
        Assert.All(b.Nodes, n => Assert.Equal(groupB, n.GroupId));

        var factB = Assert.Single(await h.FactsAsync(groupB));
        Assert.Equal([b.Episode.Uuid], factB.Episodes);
        Assert.Contains(factB.SourceNodeUuid, inB.Select(n => n.Uuid));
        var factA = Assert.Single(await h.FactsAsync(groupA));
        Assert.Equal([a.Episode.Uuid], factA.Episodes);
    }

    [SkippableTheory]
    [MemberData(nameof(Drivers.All), MemberType = typeof(Drivers))]
    public async Task One_search_names_several_groups_and_results_carry_their_group_id_and_episode_ids(string driver)
    {
        await using var h = await Harness.CreateAsync(driver, new ScriptedLlm(AliceAcme, BobGlobex));
        var groupA = Harness.NewGroup();
        var groupB = Harness.NewGroup();
        var other = Harness.NewGroup();
        var a = await h.AddAsync(groupA, AliceAcme.Body, Jan);
        var b = await h.AddAsync(groupB, BobGlobex.Body, Jan);
        await h.AddAsync(other, BobGlobex.Body, Jan); // a group not named in the search

        var facts = await h.Graphiti.SearchAsync("Alice Bob works Acme Globex", groupIds: [groupA, groupB]);

        var fromA = Assert.Single(facts, e => e.GroupId == groupA);
        Assert.Equal("Alice works at Acme.", fromA.Fact);
        Assert.Equal([a.Episode.Uuid], fromA.Episodes);
        var fromB = Assert.Single(facts, e => e.GroupId == groupB);
        Assert.Equal([b.Episode.Uuid], fromB.Episodes);
        Assert.DoesNotContain(facts, e => e.GroupId == other);

        // Entities carry their group, and with an access filter the episodes behind them.
        var results = await h.Graphiti.SearchAsync("Alice Bob Acme Globex", Everything, [groupA, groupB],
            episodeAccessFilter: new HideEpisodes());
        Assert.Contains(results.Nodes, n => n.GroupId == groupA);
        Assert.Contains(results.Nodes, n => n.GroupId == groupB);
        Assert.NotNull(results.NodeEpisodeUuids);
        Assert.All(results.Nodes, n => Assert.Equal(
            [n.GroupId == groupA ? a.Episode.Uuid : b.Episode.Uuid], results.NodeEpisodeUuids[n.Uuid]));
    }

    [SkippableTheory]
    [MemberData(nameof(Drivers.All), MemberType = typeof(Drivers))]
    public async Task Host_data_goes_in_episode_metadata(string driver)
    {
        await using var h = await Harness.CreateAsync(driver, new ScriptedLlm(AliceAcme));
        var group = Harness.NewGroup();
        var metadata = new Dictionary<string, object?> { ["tenant"] = "t-7", ["message_id"] = "m-1001" };

        var added = await h.AddAsync(group, AliceAcme.Body, Jan, metadata);

        var stored = await h.Graphiti.Nodes.Episode.GetByUuidAsync(added.Episode.Uuid);
        Assert.NotNull(stored.EpisodeMetadata);
        Assert.Equal("t-7", stored.EpisodeMetadata["tenant"]?.ToString());
        Assert.Equal("m-1001", stored.EpisodeMetadata["message_id"]?.ToString());
    }

    [SkippableTheory]
    [MemberData(nameof(Drivers.All), MemberType = typeof(Drivers))]
    public async Task Cancellation_is_honoured_between_steps(string driver)
    {
        using var cancellation = new CancellationTokenSource();
        var llm = new ScriptedLlm(AliceAcme)
        {
            // Cancel while entity extraction runs; the step finishes, the next must not start.
            BeforeAnswer = async (prompt, _) =>
            {
                if (prompt == "extract_nodes.extract_message")
                {
                    await cancellation.CancelAsync();
                }
            },
        };
        await using var h = await Harness.CreateAsync(driver, llm);
        var group = Harness.NewGroup();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.AddAsync(group, AliceAcme.Body, Jan, cancellationToken: cancellation.Token));

        Assert.Equal(["extract_nodes.extract_message"], h.Llm.Calls.Select(c => c.PromptName));
        Assert.Empty(await h.EpisodesAsync(group));
        Assert.Empty(await h.EntitiesAsync(group));
    }

    [SkippableTheory]
    [MemberData(nameof(Drivers.All), MemberType = typeof(Drivers))]
    public async Task A_budget_hook_can_refuse_a_model_call_and_the_episode_stays_saved_unprocessed_and_can_be_processed_later(
        string driver)
    {
        var budgetLeft = false;
        var refused = new List<ModelCallInfo>();
        var gate = new DelegateModelCallGate((call, _) =>
        {
            if (!budgetLeft)
            {
                refused.Add(call);
            }

            return ValueTask.FromResult(budgetLeft);
        });
        await using var h = await Harness.CreateAsync(driver, new ScriptedLlm(AliceAcme), gate: gate);
        var group = Harness.NewGroup();

        var error = await Assert.ThrowsAsync<ModelCallRefusedException>(
            () => h.AddAsync(group, AliceAcme.Body, Jan, new Dictionary<string, object?> { ["tenant"] = "t-7" }));

        Assert.Empty(h.Llm.Calls);
        Assert.Equal(group, error.Call.GroupId);
        Assert.Equal("t-7", error.Call.EpisodeMetadata?["tenant"]);
        Assert.NotNull(error.EpisodeUuid);
        var saved = Assert.Single(await h.EpisodesAsync(group));
        Assert.Equal(error.EpisodeUuid, saved.Uuid);
        Assert.Equal(AliceAcme.Body, saved.Content);
        Assert.Empty(saved.EntityEdges);
        Assert.Empty(await h.EntitiesAsync(group));
        Assert.Empty(await h.FactsAsync(group));

        // Later, with budget: process the stored episode by its id.
        budgetLeft = true;
        var processed = await h.AddAsync(group, "", Jan, uuid: error.EpisodeUuid);

        Assert.Equal(error.EpisodeUuid, processed.Episode.Uuid);
        Assert.Equal(error.EpisodeUuid, Assert.Single(await h.EpisodesAsync(group)).Uuid);
        var fact = Assert.Single(await h.Graphiti.SearchAsync("Alice works at Acme", groupIds: [group]));
        Assert.Equal([error.EpisodeUuid!], fact.Episodes);
    }
}
