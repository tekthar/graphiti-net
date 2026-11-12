// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Driver.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using static GraphitiNet.Acceptance.Tests.Support;

namespace GraphitiNet.Acceptance.Tests;

/// <summary>
/// Criteria of tekthar/graphiti-net#1 that only concern PostgreSQL storage (the in-memory driver has no tables and
/// one keyword analyzer).
/// </summary>
public class PostgreSqlTests
{
    private static readonly ScriptedEpisode AliceAcme = new(
        "Alice works at Acme.",
        ["Alice", "Acme"],
        [new("Alice", "WORKS_AT", "Acme", "Alice works at Acme.")]);

    private static readonly ScriptedEpisode Arabic = new(
        "تعمل سارة في شركة النور للبرمجيات في القاهرة.",
        ["سارة", "شركة النور", "القاهرة"],
        [new("سارة", "WORKS_AT", "شركة النور", "سارة تعمل في شركة النور.")]);

    [SkippableFact]
    public async Task The_ModelBuilder_extension_takes_a_schema_name_and_table_prefix()
    {
        Postgres.SkipIfUnavailable();
        var schema = Postgres.NewSchema();
        const string prefix = "abp_graph_";
        var options = Postgres.Options(schema, tablePrefix: prefix);
        var dbOptions = new DbContextOptionsBuilder<HostDbContext>()
            .UseNpgsql(Postgres.ConnectionString, o => o.UseVector())
            .ReplaceService<IModelCacheKeyFactory, HostModelCacheKeyFactory>()
            .Options;
        var driver = new PostgresDriver(() => new HostDbContext(dbOptions, options), options);
        await using var h = await Harness.CreateAsync(driver, new ScriptedLlm(AliceAcme), schema: schema);
        var group = Harness.NewGroup();

        await h.AddAsync(group, AliceAcme.Body, Jan);
        var fact = Assert.Single(await h.Graphiti.SearchAsync("Alice works at Acme", groupIds: [group]));
        Assert.Equal("Alice works at Acme.", fact.Fact);

        var tables = await Postgres.QueryStringsAsync(
            "SELECT table_name::text FROM information_schema.tables WHERE table_schema = $1", schema);
        Assert.Contains(prefix + "entities", tables);
        Assert.Contains(prefix + "episodes", tables);
        Assert.All(tables, table => Assert.StartsWith(prefix, table, StringComparison.Ordinal));

        await using var context = new HostDbContext(dbOptions, options);
        Assert.Contains($"CREATE TABLE {schema}.{prefix}entities", context.Database.GenerateCreateScript(),
            StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Keyword_search_language_is_set_per_group()
    {
        Postgres.SkipIfUnavailable();
        var arabicGroup = Harness.NewGroup();
        var simpleGroup = Harness.NewGroup();
        await using var h = await Harness.CreateAsync(Drivers.PostgreSql, new ScriptedLlm(Arabic),
            textSearchConfigs: new Dictionary<string, string> { [arabicGroup] = "arabic" });
        await h.AddAsync(arabicGroup, Arabic.Body, Jan);
        await h.AddAsync(simpleGroup, Arabic.Body, Jan);

        // "الشركة" (the company) is "شركة" with the article: the arabic configuration stems both to one lexeme, the
        // default simple configuration does not.
        var results = await h.Graphiti.SearchAsync("الشركة", KeywordOnly, [arabicGroup, simpleGroup]);

        Assert.Contains(results.Edges, e => e.GroupId == arabicGroup && e.Fact == "سارة تعمل في شركة النور.");
        Assert.DoesNotContain(results.Edges, e => e.GroupId == simpleGroup);
        Assert.Contains(results.Episodes, e => e.GroupId == arabicGroup);
        Assert.DoesNotContain(results.Episodes, e => e.GroupId == simpleGroup);

        var configs = await Postgres.QueryStringsAsync(
            $"SELECT DISTINCT group_id || '=' || text_search_config::text FROM \"{h.Schema}\".entity_edges");
        Assert.Equal([$"{arabicGroup}=arabic", $"{simpleGroup}=simple"], configs.Order());
    }

    /// <summary>An application context that hosts the graph tables next to its own.</summary>
    private sealed class HostDbContext(DbContextOptions<HostDbContext> options, PostgresGraphOptions graphOptions)
        : DbContext(options)
    {
        public PostgresGraphOptions GraphOptions => graphOptions;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Note>().ToTable("notes", graphOptions.Schema);
            modelBuilder.AddGraphiti(graphOptions);
        }
    }

    private sealed class HostModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            (context.GetType(), ((HostDbContext)context).GraphOptions.Schema,
                ((HostDbContext)context).GraphOptions.TablePrefix, designTime);
    }

    private sealed class Note
    {
        public int Id { get; set; }

        public string Text { get; set; } = "";
    }
}
