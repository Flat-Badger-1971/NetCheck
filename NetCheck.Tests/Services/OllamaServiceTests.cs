using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NetCheck.Services;
using NSubstitute;
using Xunit;

namespace NetCheck.Tests.Services;

public class OllamaServiceTests
{
    private static OllamaService CreateService(out ILogger<OllamaService> logger)
    {
        logger = Substitute.For<ILogger<OllamaService>>();
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string>
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
        OllamaService service = CreateService(out _);
        // Use a clearly invalid endpoint via reflection to force failure quickly
        FieldInfo endpointField = typeof(OllamaService).GetField("_endpoint", BindingFlags.NonPublic | BindingFlags.Instance);
        endpointField!.SetValue(service, "http://localhost:59999"); // assuming nothing listening

        bool available = await service.IsModelAvailableAsync();
        Assert.False(available);
    }
}
