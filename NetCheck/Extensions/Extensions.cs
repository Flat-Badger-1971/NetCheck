using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using OllamaSharp;
using System;
using System.Threading.Tasks;
using NetCheck.Services;
using System.Collections.Generic;

namespace NetCheck.Extensions;

public static class Extensions
{
    public static WebApplicationBuilder AddAIServices(this WebApplicationBuilder builder)
    {
        // Register Ollama service first
        builder.Services.AddSingleton<IOllamaModelService, OllamaService>();

        ILoggerFactory logger = builder.Services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        string ollamaEndPoint = builder.Configuration["AI:Ollama:Endpoint"];
        string modelName = builder.Configuration["AI:Ollama:Model"] ?? "llama3.2:3b";

        if (!string.IsNullOrWhiteSpace(ollamaEndPoint))
        {
            // Create a temporary service provider to get the Ollama service
            ServiceProvider tempServiceProvider = builder.Services.BuildServiceProvider();
            IOllamaModelService ollamaService = tempServiceProvider.GetRequiredService<IOllamaModelService>();

            // Ensure model is loaded before proceeding
            Task<bool> modelLoadTask = Task.Run(async () =>
            {
                bool isLoaded = await ollamaService.EnsureModelIsLoadedAsync();

                if (!isLoaded)
                {
                    ILogger<OllamaService> extensionsLogger = tempServiceProvider.GetRequiredService<ILogger<OllamaService>>();
                    extensionsLogger.LogWarning("Failed to ensure model {ModelName} is loaded. AI features may not work properly.", modelName);
                }

                return isLoaded;
            });

            // Wait for model loading (with timeout)
            try
            {
                modelLoadTask.Wait(TimeSpan.FromMinutes(10)); // 10 minute timeout for model loading
            }
            catch (Exception ex)
            {
                ILogger<OllamaService> extensionsLogger = tempServiceProvider.GetRequiredService<ILogger<OllamaService>>();
                extensionsLogger.LogError(ex, "Timeout or error while loading model {ModelName}", modelName);
            }

            IChatClient client = new OllamaApiClient(ollamaEndPoint, modelName);

            builder.Services.AddChatClient(services =>
                client
                .AsBuilder()
                .UseFunctionInvocation()
                .UseDistributedCache(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())))
                .UseOpenTelemetry()
                .UseLogging(logger)
                .Build(services));

            // Register MCP client using SseClientTransport for remote server, fallback to StdioClientTransport
            builder.Services.AddSingleton(serviceProvider =>
            {
                ILoggerFactory spLoggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
                ILogger spLogger = spLoggerFactory.CreateLogger("McpClientSetup");

                string mcpUrl = builder.Configuration["AI:Mcp:Url"];
                string mcpToken = builder.Configuration["AI:Mcp:Token"];

                try
                {
                    IClientTransport transport;
                    if (!string.IsNullOrWhiteSpace(mcpUrl))
                    {
                        SseClientTransportOptions sseOptions = new SseClientTransportOptions
                        {
                            Endpoint = new Uri(mcpUrl),
                            AdditionalHeaders = !string.IsNullOrWhiteSpace(mcpToken)
                                ? new Dictionary<string, string>
                                {
                                    { "Authorization", $"Bearer {mcpToken}" }
                                }
                                : null
                        };
                        spLogger.LogInformation("Using SSE transport for MCP client with endpoint {Endpoint}", mcpUrl);
                        transport = new SseClientTransport(sseOptions);
                    }
                    else
                    {
                        string mcpCommand = builder.Configuration["AI:Mcp:Command"] ?? "github-mcp-server";
                        string mcpArgsRaw = builder.Configuration["AI:Mcp:Arguments"] ?? string.Empty;
                        string[] mcpArgs = string.IsNullOrWhiteSpace(mcpArgsRaw)
                            ? []
                            : mcpArgsRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        StdioClientTransportOptions stdioOptions = new StdioClientTransportOptions
                        {
                            Command = mcpCommand,
                            Arguments = mcpArgs
                        };

                        spLogger.LogInformation("Using Stdio transport for MCP client with command '{Command}'", mcpCommand);
                        transport = new StdioClientTransport(stdioOptions);
                    }
                    IMcpClient mcpClient = McpClientFactory.CreateAsync(transport).GetAwaiter().GetResult();
                    spLogger.LogInformation("MCP client created and connected successfully.");
                    return mcpClient;
                }
                catch (Exception ex)
                {
                    spLogger.LogError(ex, "Failed to create MCP client");
                    throw;
                }
            });
        }

        return builder;
    }
}
