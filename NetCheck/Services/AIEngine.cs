using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using NetCheck.Tools;
using NetCheck.Utility;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace NetCheck.Services;

public class AIEngine(
    IChatClient chatClient,
    IMcpClient mcpClient,
    ILogger<AIEngine> logger) : IAIEngine
{
    public async Task<string> RunAgent()
    {
        List<ChatMessage> conversation = [];

        conversation.Add(new(ChatRole.System, GetSystemPrompt()));

        List<McpClientTool> mcpTools = (await mcpClient.ListToolsAsync(null, default)).ToList();

        // ollama only - ollama seems to have a low limit of the number of tools it can handle
        mcpTools = mcpTools
            .Where(t => t.Name == "list_branches" || t.Name == "get_file_contents" || t.Name == "search_code")
            .ToList();

        // you can add your own local tools here if you want
        List<AIFunction> localTools = ParseTools.GetLocalTools();

        ChatOptions chatOptions = new()
        {
            Tools = [.. mcpTools, .. localTools],
            AllowMultipleToolCalls = true,
            ToolMode = ChatToolMode.RequireAny
        };

        Console.WriteLine("Available tools (MCP + local):");

        foreach (AITool tool in chatOptions.Tools)
        {
            Console.WriteLine($"{tool.Name} - {tool.Description}");
        }

        StringBuilder userPrompt = new StringBuilder();

        userPrompt.AppendLine("Give me the branch names for this github repository:");
        // this is my personal repo for testing to avoid permissions issues with private repos
        userPrompt.AppendLine($"TARGET REPOSITORY: NetCheck");
        userPrompt.AppendLine($"TARGET OWNER: Flat-Badger-1971");
        userPrompt.AppendLine("The response must only contain a JSON formatted array of branch names and NO other text.");
        userPrompt.AppendLine("For example [\"main\", \"dev\", \"feature-xyz\"]");

        conversation.Add(new ChatMessage(ChatRole.User, userPrompt.ToString()));

        Console.WriteLine();

        StringBuilder updates = new StringBuilder();

        logger.LogDebug("Starting streaming response");

        await foreach (ChatResponseUpdate update in chatClient.GetStreamingResponseAsync(conversation, chatOptions))
        {
            Console.Write(update);
            updates.Append(update.Text);
        }

        logger.LogDebug("\nCompleted streaming response");

        conversation.Add(new ChatMessage(ChatRole.Assistant, updates.ToString()));

        int tokenestimate = await TokenEstimator.EstimateLlamaConversationTokens(conversation);

        updates.AppendLine($"\n\nEstimated tokens used: {tokenestimate}");

        return updates.ToString();
    }

    private string GetSystemPrompt()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Prompts", "system.txt");

            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }

            logger.LogWarning("System prompt file not found at {Path}. Using fallback.", path);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load system prompt.");
        }

        return "You are an AI agent.";
    }
}
