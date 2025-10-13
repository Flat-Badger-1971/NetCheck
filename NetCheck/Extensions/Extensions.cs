using System;
using System.Collections.Generic;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using NetCheck.Services;
using OllamaSharp;

namespace NetCheck.Extensions;

// ignore warning about commented out code
public static class Extensions
{
#pragma warning disable S125 // commented out code warning
    public static WebApplicationBuilder AddAIServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IOllamaModelService, OllamaService>();
        AddMcpClient(builder);
        AddOllamaChatClient(builder); // if using Azure OpenAI use AddOpenAIChatClient(builder) instead;

        return builder;
    }
#pragma warning restore S125

    private static void AddOllamaChatClient(WebApplicationBuilder builder)
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

            IChatClient ollamaChatClient = new OllamaApiClient(endpoint, model);

            ChatClientBuilder chatBuilder = ollamaChatClient.AsBuilder()
                .UseFunctionInvocation()
                .UseLogging(loggerFactory);

            return chatBuilder.Build(sp);
        });
    }

#pragma warning disable S1144 // unused method warning
    private static void AddOpenAIChatClient(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IChatClient>(sp =>
        {
            ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            ILogger logger = loggerFactory.CreateLogger("ChatClientInit");
            IConfiguration cfg = sp.GetRequiredService<IConfiguration>();

            if (string.IsNullOrWhiteSpace(cfg["AI:OpenAI:Url"]) || string.IsNullOrWhiteSpace(cfg["AI:OpenAI:Key"]))
            {
                logger.LogError("OpenAI configuration is missing (AI:OpenAI:Url and AI:OpenAI:Key are required).");
                throw new InvalidOperationException("OpenAI configuration is missing (AI:OpenAI:Url and AI:OpenAI:Key are required).");
            }

            Uri endpoint = new(cfg["AI:OpenAI:Url"]);
            AzureKeyCredential chatKey = new(cfg["AI:OpenAI:Key"]);

            AzureOpenAIClient azureClient = new AzureOpenAIClient(endpoint, chatKey);
            IChatClient client = azureClient.GetChatClient(cfg["AI:OpenAI:Model"]).AsIChatClient();

            ChatClientBuilder chatBuilder =
                client
                .AsBuilder()
                .UseFunctionInvocation()
                .UseLogging(loggerFactory);

            return chatBuilder.Build(sp);
        });
    }
#pragma warning restore S1144

    private static void AddMcpClient(WebApplicationBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration["AI:Mcp:Url"]) &&
            string.IsNullOrWhiteSpace(builder.Configuration["AI:Mcp:Command"]))
        {
            return;
        }

        builder.Services.AddSingleton<McpClient>(sp =>
        {
            ILogger logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("McpInit");
            IConfiguration cfg = sp.GetRequiredService<IConfiguration>();
            string mcpUrl = cfg["AI:Mcp:Url"];
            string mcpToken = cfg["AI:Mcp:Token"];

            try
            {
                // determine the transport type - http for urls or stdio for local commands
                IClientTransport transport;
                Dictionary<string, string> headers = new()
                {
                    { "Authorization", $"Bearer {mcpToken}" },
                    // this will automatically exclude any tools that require write access
                    // { "X-MCP-Readonly", "true" }
                };

                if (!string.IsNullOrWhiteSpace(mcpUrl))
                {
                    HttpClientTransportOptions http = new()
                    {
                        Endpoint = new Uri(mcpUrl),
                        AdditionalHeaders = !string.IsNullOrWhiteSpace(mcpToken) ? headers : null
                    };

                    logger.LogInformation("Using HTTP transport for MCP endpoint {Endpoint}", mcpUrl);
                    transport = new HttpClientTransport(http);
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

                McpClient client = McpClient.CreateAsync(transport).GetAwaiter().GetResult();
                logger.LogInformation("MCP client connected.");

                return client;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to initialise MCP client.");
                throw;
            }
        });
    }
}
