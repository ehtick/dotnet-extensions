﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.ClientModel;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using OpenAI;
using OpenAI.Assistants;
using Xunit;

#pragma warning disable S103 // Lines should not be too long

namespace Microsoft.Extensions.AI;

public class OpenAIAssistantChatClientTests
{
    [Fact]
    public void AsIChatClient_InvalidArgs_Throws()
    {
        Assert.Throws<ArgumentNullException>("assistantClient", () => ((AssistantClient)null!).AsIChatClient("assistantId"));
        Assert.Throws<ArgumentNullException>("assistantId", () => new AssistantClient("ignored").AsIChatClient((string)null!));
        Assert.Throws<ArgumentNullException>("assistant", () => new AssistantClient("ignored").AsIChatClient((Assistant)null!));
    }

    [Fact]
    public void AsIChatClient_OpenAIClient_ProducesExpectedMetadata()
    {
        Uri endpoint = new("http://localhost/some/endpoint");

        var client = new OpenAIClient(new ApiKeyCredential("key"), new OpenAIClientOptions { Endpoint = endpoint });

        IChatClient[] clients =
        [
            client.GetAssistantClient().AsIChatClient("assistantId"),
            client.GetAssistantClient().AsIChatClient("assistantId", "threadId"),
        ];

        foreach (var chatClient in clients)
        {
            var metadata = chatClient.GetService<ChatClientMetadata>();
            Assert.Equal("openai", metadata?.ProviderName);
            Assert.Equal(endpoint, metadata?.ProviderUri);
        }
    }

    [Fact]
    public void GetService_AssistantClient_SuccessfullyReturnsUnderlyingClient()
    {
        AssistantClient assistantClient = new OpenAIClient("key").GetAssistantClient();
        IChatClient chatClient = assistantClient.AsIChatClient("assistantId");

        Assert.Same(assistantClient, chatClient.GetService<AssistantClient>());

        Assert.Null(chatClient.GetService<OpenAIClient>());

        using IChatClient pipeline = chatClient
            .AsBuilder()
            .UseFunctionInvocation()
            .UseOpenTelemetry()
            .UseDistributedCache(new MemoryDistributedCache(Options.Options.Create(new MemoryDistributedCacheOptions())))
            .Build();

        Assert.NotNull(pipeline.GetService<FunctionInvokingChatClient>());
        Assert.NotNull(pipeline.GetService<DistributedCachingChatClient>());
        Assert.NotNull(pipeline.GetService<CachingChatClient>());
        Assert.NotNull(pipeline.GetService<OpenTelemetryChatClient>());

        Assert.Same(assistantClient, pipeline.GetService<AssistantClient>());
        Assert.IsType<FunctionInvokingChatClient>(pipeline.GetService<IChatClient>());
    }
}
