using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NetCheck.Services;
using Xunit;

namespace NetCheck.Tests.Services;

public class OllamaServiceTests
{
    private OllamaService CreateService(out ILogger<OllamaService> logger)
    {
        logger = Substitute.For<ILogger<OllamaService>>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["AI:Ollama:Endpoint"] = "http://localhost:11434",
            ["AI:Ollama:Model"] = "llama3.2:8b"
        }).Build();

        return new OllamaService(config, logger);
    }

    [Fact]
    public async Task IsModelAvailableAsync_ReturnsFalse_WhenApiUnavailable()
    {
        // We cannot inject a fake HttpClient without refactor; we rely on failure path when endpoint not reachable.
        var service = CreateService(out _);
        // Use a clearly invalid endpoint via reflection to force failure quickly
        var endpointField = typeof(OllamaService).GetField("_endpoint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        endpointField!.SetValue(service, "http://localhost:59999"); // assuming nothing listening

        bool available = await service.IsModelAvailableAsync();
        Assert.False(available);
    }
}
