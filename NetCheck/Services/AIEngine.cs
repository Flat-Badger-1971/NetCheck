using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using NetCheck.Utility;
using OllamaSharp;
using OllamaSharp.Models;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace NetCheck.Services;

public class AIEngine : IAIEngine
{
    private readonly IChatClient _chatClient;
    private readonly IOllamaModelService _ollamaService;
    private readonly ILogger<AIEngine> _logger;
    private readonly IMcpClient _mcpClient;
    private readonly List<ChatMessage> _systemMessages;
    private static readonly Regex s_jsonObjectRegex = new(@"\{(?:[^{}""']|""(?:\\.|[^""])*""|'(?:\\.|[^'])*')*?\}", RegexOptions.Singleline | RegexOptions.Compiled);

    public AIEngine(
        IChatClient chatClient,
        IMcpClient mcpClient,
        IOllamaModelService ollamaService,
        ILogger<AIEngine> logger)
    {
        _chatClient = chatClient;
        _ollamaService = ollamaService;
        _logger = logger;
        _mcpClient = mcpClient;

        _systemMessages =
        [
            new ChatMessage(ChatRole.System, GetSystemPrompt())
        ];
    }

    public async Task Interactive()
    {
        List<ChatMessage> conversation = [];

        conversation.Add(new(ChatRole.System, GetSystemPrompt()));

        IList<McpClientTool> tools = await _mcpClient.ListToolsAsync(null, default);
        ChatOptions chatOptions = new() { Tools = [.. tools], AllowMultipleToolCalls=true, ToolMode = ChatToolMode.RequireAny };
        chatOptions.AddOllamaOption(OllamaOption.NumCtx, 8192);

        Console.WriteLine("Available tools:");

        foreach (McpClientTool tool in tools)
        {
            Console.WriteLine($"{tool.Name}: {tool.Description}");
        }

        Console.WriteLine();

        while (true)
        {
            Console.Write("Prompt: ");

            string inputText = Console.ReadLine();

            if (inputText.Equals("exit", StringComparison.InvariantCultureIgnoreCase))
            {
                break;
            }

            conversation.Add(new(ChatRole.User, inputText));

            List<ChatResponseUpdate> updates = [];

            await foreach (ChatResponseUpdate update in _chatClient.GetStreamingResponseAsync(conversation, chatOptions))
            {
                Console.Write(update);
                updates.Add(update);
            }

            Console.WriteLine();
            Console.WriteLine($"Tokens used estimate: {TokenEstimator.EstimateConversationTokens(conversation)}");
            conversation.AddMessages(updates);
        }
    }

    public async Task<string> ScanRepositoryAsync(string repository, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            throw new ArgumentException("Repository identifier is required.", nameof(repository));
        }

        if (!await _ollamaService.IsModelAvailableAsync() &&
            !await _ollamaService.EnsureModelIsLoadedAsync())
        {
            _logger.LogError("Model unavailable for repository scan.");
            throw new InvalidOperationException("Model not available.");
        }

        string repoNormalised = Uri.UnescapeDataString(repository);
        IList<McpClientTool> tools = await _mcpClient.ListToolsAsync(null, cancellationToken);

        string message = $"Discovered {tools.Count} MCP tools";
        _logger.LogInformation(message);

        string toolList = string.Join('\n', tools.Select(t => $"- {t.Name}: {t.Description}"));
        StringBuilder userPrompt = new StringBuilder();

        userPrompt.AppendLine($"TARGET REPOSITORY: {repoNormalised}");
        userPrompt.AppendLine("You MUST discover .NET versions ONLY by invoking the available tools. NO GUESSING. NO PROSE.");
        userPrompt.AppendLine("AVAILABLE TOOLS:");
        userPrompt.AppendLine(toolList);
        userPrompt.AppendLine("PROTOCOL (STRICT - one JSON object per turn, nothing else):");
        userPrompt.AppendLine("Tool call example:");
        userPrompt.AppendLine("{\"action\":\"call_tool\",\"tool\":\"<toolName>\",\"arguments\":{ },\"reason\":\"why this tool is needed\"}");
        userPrompt.AppendLine("Final result signal example:");
        userPrompt.AppendLine("{\"action\":\"final_result\"}");
        userPrompt.AppendLine("You MUST read (if they exist): global.json, every *.csproj, Directory.Build.props / .targets, Dockerfile*, *.yml / *.yaml.");
        userPrompt.AppendLine("DO NOT produce the final schema JSON. Only signal final_result first; I will then give you all evidence to produce the JSON in a second pass.");
        userPrompt.AppendLine("RULES:");
        userPrompt.AppendLine("- No markdown");
        userPrompt.AppendLine("- No explanations outside JSON");
        userPrompt.AppendLine("- Always include 'action'");
        userPrompt.AppendLine("- Use 'arguments': {} if none");
        userPrompt.AppendLine("Start by listing the repository root with an appropriate tool call. Then use any other tool calls as necessary.");

        List<ChatMessage> conversation = new(_systemMessages)
        {
            new ChatMessage(ChatRole.User, userPrompt.ToString())
        };

        ChatOptions options = new() { Tools = [.. tools] };

        cancellationToken.ThrowIfCancellationRequested();

        ChatResponse response = await _chatClient.GetResponseAsync(conversation, options, cancellationToken);

        _logger.LogInformation(response.Messages.ToString());

        string assistant = ExtractAssistantText(response);
        string planJson = ExtractSingleJsonObject(assistant);

        return planJson;
    }

    private static object JsonToPlain(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => JsonToPlain(p.Value), StringComparer.OrdinalIgnoreCase),
            JsonValueKind.Array => el.EnumerateArray().Select(JsonToPlain).ToList(),
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out long l) ? l : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => el.ToString()
        };

    private static string ExtractSingleJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        foreach (Match m in s_jsonObjectRegex.Matches(raw))
        {
            string candidate = m.Value.Trim();

            if (IsValidJson(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsValidJson(string candidate)
    {
        try
        {
            using (JsonDocument _ = JsonDocument.Parse(candidate))
            {
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    private static string ExtractAssistantText(ChatResponse response)
    {
        if (response.Messages != null && response.Messages.Count > 0)
        {
            for (int i = response.Messages.Count - 1; i >= 0; i--)
            {
                ChatMessage msg = response.Messages[i];

                if (msg.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(msg.Text))
                {
                    return msg.Text;
                }
            }
        }

        return response.Text ?? string.Empty;
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

            _logger.LogWarning("System prompt file not found at {Path}. Using fallback.", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load system prompt.");
        }

        return "You are an AI agent.";
    }
}
