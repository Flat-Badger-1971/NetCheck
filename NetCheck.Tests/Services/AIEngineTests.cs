using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using NetCheck.Services;
using NSubstitute;
using Xunit;

namespace NetCheck.Tests.Services;

public class AIEngineTests
{
    private static AIEngine CreateEngine()
    {
        IChatClient chatClient = Substitute.For<IChatClient>();
        McpClient mcpClient = Substitute.For<McpClient>();
        ILogger<AIEngine> logger = Substitute.For<ILogger<AIEngine>>();

        return new AIEngine(chatClient, mcpClient, logger);
    }

    [Fact]
    public void BuildConsolidatedJson_ProducesExpectedShape()
    {
        AIEngine engine = CreateEngine();
        string pullRequests = "[{\"PullRequestNumber\":1,\"Title\":\"TB-1 test\",\"Body\":\"Body\"}]";
        string titleFailures = "[]";
        string bodyFailures = "[]";
        int tokens = 42;

        MethodInfo method = typeof(AIEngine).GetMethod("BuildConsolidatedJson", BindingFlags.NonPublic | BindingFlags.Instance);
        string consolidated = (string)method.Invoke(engine, [pullRequests, titleFailures, bodyFailures, tokens, true]);

        using (JsonDocument doc = JsonDocument.Parse(consolidated))
        {
            Assert.True(doc.RootElement.TryGetProperty("Repository", out JsonElement repo));
            Assert.Equal("Flat-Badger-1971", repo.GetProperty("Owner").GetString());
            Assert.True(doc.RootElement.TryGetProperty("PullRequests", out JsonElement prs));
            Assert.Equal(1, prs.GetArrayLength());
            Assert.True(doc.RootElement.TryGetProperty("Stats", out JsonElement stats));
            Assert.Equal(42, stats.GetProperty("TokenEstimate").GetInt32());
        }
    }

    [Fact]
    public void CountArrayItems_ReturnsZero_OnInvalidJson()
    {
        AIEngine engine = CreateEngine();
        MethodInfo method = typeof(AIEngine).GetMethod("CountArrayItems", BindingFlags.NonPublic | BindingFlags.Instance);
        int count = (int)method.Invoke(engine, ["not json"]);
        Assert.Equal(0, count);
    }
}
