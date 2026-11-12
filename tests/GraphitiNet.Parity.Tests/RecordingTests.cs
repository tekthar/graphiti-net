// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

namespace GraphitiNet.Parity.Tests;

public class RecordingTests
{
    [Theory]
    [InlineData("customer-support")]
    [InlineData("arabic")]
    public void Keys_computed_in_dotnet_match_the_python_recorder(string dataset)
    {
        var recording = Recording.Load(dataset);

        Assert.NotEmpty(recording.Calls);
        foreach (var (key, call) in recording.Calls)
        {
            Assert.Equal(key, Recording.Key(call.Messages));
        }
    }

    [Theory]
    [InlineData("customer-support")]
    [InlineData("arabic")]
    public void Embeddings_decode_to_the_recorded_size(string dataset)
    {
        var recording = Recording.Load(dataset);

        Assert.NotEmpty(recording.Embeddings);
        Assert.All(recording.Embeddings.Values, v => Assert.Equal(recording.EmbeddingDim, v.Length));
    }
}
