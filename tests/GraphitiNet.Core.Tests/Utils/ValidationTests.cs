// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using GraphitiNet.Utils;

namespace GraphitiNet.Tests.Utils;

public class ValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tenant_42")]
    [InlineData("user-abc-123")]
    [InlineData("ABC")]
    public void Valid_group_ids_pass(string? groupId)
    {
        Validation.ValidateGroupId(groupId);
    }

    [Theory]
    [InlineData("group id")]
    [InlineData("a'b")]
    [InlineData("a/b")]
    [InlineData("café")]
    [InlineData("abc\n")]
    [InlineData("g\" OR 1=1 --")]
    public void Invalid_group_ids_throw(string groupId)
    {
        var ex = Assert.Throws<GroupIdValidationException>(() => Validation.ValidateGroupId(groupId));
        Assert.Contains("must contain only alphanumeric", ex.Message, StringComparison.Ordinal);
        Assert.Equal(groupId, ex.GroupId);
    }

    [Fact]
    public void Group_id_lists_are_checked_item_by_item()
    {
        Validation.ValidateGroupIds(null);
        Validation.ValidateGroupIds(["a", "b-c"]);
        Assert.Throws<GroupIdValidationException>(() => Validation.ValidateGroupIds(["a", "b c"]));
    }

    [Theory]
    [InlineData("Person")]
    [InlineData("_private")]
    [InlineData("Entity2")]
    public void Valid_labels_pass(string label)
    {
        Validation.ValidateNodeLabels([label]);
    }

    [Fact]
    public void All_invalid_labels_are_reported()
    {
        var ex = Assert.Throws<NodeLabelValidationException>(() =>
            Validation.ValidateNodeLabels(["Person", "2fast", "has space", "Ok_1"]));

        Assert.Equal(["2fast", "has space"], ex.NodeLabels);
        Assert.Equal(
            "node_labels must start with a letter or underscore and contain only alphanumeric characters or "
            + "underscores: \"2fast\", \"has space\"",
            ex.Message);
    }

    [Fact]
    public void Error_messages_match_upstream()
    {
        Assert.Equal("edge abc not found", new EdgeNotFoundException("abc").Message);
        Assert.Equal("None of the edges for ['a', 'b'] were found.", new EdgesNotFoundException(["a", "b"]).Message);
        Assert.Equal("no nodes found for group ids ['g']", new GroupsNodesNotFoundException(["g"]).Message);
    }
}
