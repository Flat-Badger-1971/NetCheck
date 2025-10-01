using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NetCheck.Services;
using ModelContextProtocol.Client;
using Xunit;
using System.Collections.Generic;

namespace NetCheck.Tests.Services;

public class AIEngineTests
{
    private static AIEngine CreateEngine()
    {
        var chatClient = Substitute.For<IChatClient>();
        var mcpClient = Substitute.For<IMcpClient>();
        var logger = Substitute.For<ILogger<AIEngine>>();
        return new AIEngine(chatClient, mcpClient, logger);
    }

    [Fact]
    public async Task BuildConsolidatedJson_ProducesExpectedShape()
    {
        var engine = CreateEngine();
        string pullRequests = "[{\"PullRequestNumber\":1,\"Title\":\"TB-1 test\",\"Body\":\"Body\"}]";
        string titleFailures = "[]";
        string bodyFailures = "[]";
        int tokens = 42;

        var method = typeof(AIEngine).GetMethod("BuildConsolidatedJson", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var consolidated = (string)method!.Invoke(engine, new object[]{ pullRequests, titleFailures, bodyFailures, tokens, true });

        using var doc = JsonDocument.Parse(consolidated);
        Assert.True(doc.RootElement.TryGetProperty("Repository", out var repo));
        Assert.Equal("Flat-Badger-1971", repo.GetProperty("Owner").GetString());
        Assert.True(doc.RootElement.TryGetProperty("PullRequests", out var prs));
        Assert.Equal(1, prs.GetArrayLength());
        Assert.True(doc.RootElement.TryGetProperty("Stats", out var stats));
        Assert.Equal(42, stats.GetProperty("TokenEstimate").GetInt32());
    }

    [Fact]
    public void CountArrayItems_ReturnsZero_OnInvalidJson()
    {
        var engine = CreateEngine();
        var method = typeof(AIEngine).GetMethod("CountArrayItems", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        int count = (int)method!.Invoke(engine, new object[]{ "not json" });
        Assert.Equal(0, count);
    }
}
