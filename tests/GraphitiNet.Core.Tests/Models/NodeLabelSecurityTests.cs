// Copyright 2024, Zep Software, Inc.
// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.
//
// Ported from tests/test_node_label_security.py (Graphiti v0.31.2) and changed for .NET.

using GraphitiNet.Models;

namespace GraphitiNet.Tests.Models;

public class NodeLabelSecurityTests
{
    private const string Injection = "Entity`) WITH n MATCH (x) DETACH DELETE x //";

    [Fact]
    public void Entity_node_rejects_unsafe_labels()
    {
        var ex = Assert.Throws<NodeLabelValidationException>(() =>
            new EntityNode { Name = "Alice", GroupId = "group", Labels = [Injection] });
        Assert.StartsWith("node_labels must start with a letter or underscore", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Entity_node_assignment_rejects_unsafe_labels()
    {
        var node = new EntityNode { Name = "Alice", GroupId = "group", Labels = ["Person"] };

        var ex = Assert.Throws<NodeLabelValidationException>(() => node.Labels = [Injection]);
        Assert.StartsWith("node_labels must start with a letter or underscore", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["Person"], node.Labels);
    }
}
