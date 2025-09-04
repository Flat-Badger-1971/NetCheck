using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using NetCheck.AI.Mcp;
using NetCheck.Services;
using OllamaSharp;

namespace NetCheck.Extensions;

public static class Extensions
{
    public static WebApplicationBuilder AddAIServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IOllamaModelService, OllamaService>();
        AddMcpClient(builder);
        AddMcpTools(builder);
        AddUnifiedChatClient(builder);

        return builder;
    }

    private static void AddUnifiedChatClient(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IChatClient>(sp =>
        {
            ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            ILogger logger = loggerFactory.CreateLogger("ChatClientInit");
            IConfiguration config = sp.GetRequiredService<IConfiguration>();
            IOllamaModelService ollamaService = sp.GetRequiredService<IOllamaModelService>();

            string endpoint = config["AI:Ollama:Endpoint"] ?? throw new InvalidOperationException("AI:Ollama:Endpoint not configured.");
            string model = config["AI:Ollama:Model"] ?? "llama3.2:3b";

            if (!ollamaService.EnsureModelIsLoadedAsync().GetAwaiter().GetResult())
            {
                logger.LogWarning("Model {Model} may not be available; continuing.", model);
            }

            IChatClient raw = new OllamaApiClient(endpoint, model);

            ChatClientBuilder chatBuilder =
                raw
                .AsBuilder()
                .UseFunctionInvocation()
                .UseDistributedCache(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())))
                .UseLogging(loggerFactory);

            return chatBuilder.Build(sp);
        });
    }

    private static void AddMcpTools(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IList<AITool>>(sp =>
        {
            ILogger logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("McpTools");
            IMcpClient mcpClient = sp.GetService<IMcpClient>();
            if (mcpClient is null)
            {
                logger.LogInformation("No IMcpClient registered; returning empty tool list.");
                return Array.Empty<AITool>().ToList();
            }

            try
            {
                (IList<AITool> tools, IDictionary<string, IMcpInvokableTool> invokers) tools = McpChatToolFactory
                    .CreateAsync(mcpClient, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                logger.LogInformation("Loaded {Count} MCP tools.", tools.tools.Count);

                return tools.tools.ToList();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load MCP tools; returning empty list.");
                return [];
            }
        });
    }

    private static void AddMcpClient(WebApplicationBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration["AI:Mcp:Url"]) &&
            string.IsNullOrWhiteSpace(builder.Configuration["AI:Mcp:Command"]))
        {
            return;
        }

        builder.Services.AddSingleton<IMcpClient>(sp =>
        {
            ILogger logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("McpInit");
            IConfiguration cfg = sp.GetRequiredService<IConfiguration>();

            string mcpUrl = cfg["AI:Mcp:Url"];
            string mcpToken = cfg["AI:Mcp:Token"];

            try
            {
                IClientTransport transport;

                if (!string.IsNullOrWhiteSpace(mcpUrl))
                {
                    SseClientTransportOptions sse = new()
                    {
                        Endpoint = new Uri(mcpUrl),
                        AdditionalHeaders = !string.IsNullOrWhiteSpace(mcpToken)
                            ? new Dictionary<string, string> { { "Authorization", $"Bearer {mcpToken}" } }
                            : null
                    };
                    logger.LogInformation("Using SSE transport for MCP endpoint {Endpoint}", mcpUrl);
                    transport = new SseClientTransport(sse);
                }
                else
                {
                    string mcpCommand = cfg["AI:Mcp:Command"] ?? "github-mcp-server";
                    string argsRaw = cfg["AI:Mcp:Arguments"] ?? string.Empty;
                    string[] args = string.IsNullOrWhiteSpace(argsRaw) ? [] : argsRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                    StdioClientTransportOptions stdio = new()
                    {
                        Command = mcpCommand,
                        Arguments = args
                    };
                    logger.LogInformation("Using Stdio transport for MCP command {Command}", mcpCommand);
                    transport = new StdioClientTransport(stdio);
                }

                IMcpClient client = McpClientFactory.CreateAsync(transport).GetAwaiter().GetResult();
                logger.LogInformation("MCP client connected.");

                return client;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to initialize MCP client.");
                throw;
            }
        });
    }
}
