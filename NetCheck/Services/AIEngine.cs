using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NetCheck.AI.Mcp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace NetCheck.Services;

public class AIEngine : IAIEngine
{
    private readonly IChatClient _chatClient;
    private readonly IOllamaModelService _ollamaService;
    private readonly ILogger<AIEngine> _logger;
    private readonly List<ChatMessage> _baseSystemMessages;
    private readonly IList<AITool> _toolDescriptors;

    public AIEngine(
        IChatClient chatClient,
        IOllamaModelService ollamaService,
        ILogger<AIEngine> logger,
        IList<AITool> toolDescriptors) //,
        // IDictionary<string, IMcpInvokableTool> _)
    {
        _chatClient = chatClient;
        _ollamaService = ollamaService;
        _logger = logger;
        _toolDescriptors = toolDescriptors;

        _baseSystemMessages =
        [
            new ChatMessage(ChatRole.System, GetSystemPrompt())
        ];
    }

    public async Task<string> ScanRepositoryAsync(string repository, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            throw new ArgumentException("Repository identifier is required.", nameof(repository));
        }

        bool available = await _ollamaService.IsModelAvailableAsync();

        if (!available)
        {
            bool ensured = await _ollamaService.EnsureModelIsLoadedAsync();

            if (!ensured)
            {
                _logger.LogError("Model unavailable for repository scan.");
                throw new InvalidOperationException("Model not available.");
            }
        }

        List<ChatMessage> messages = new List<ChatMessage>(_baseSystemMessages)
        {
            new ChatMessage(
                ChatRole.User,
                "Repository target: " + repository + "\n\n" +
                "Perform the complete .NET version scan (DO NOT stop after listing repository metadata):\n" +
                "1. Traverse the repository using available tools until all relevant files are examined.\n" +
                "2. Collect data from: global.json, *.csproj, *.fsproj, *.vbproj, Directory.Build.props, Directory.Build.targets, Dockerfile*, *.yml, *.yaml.\n" +
                "3. Extract and deduplicate:\n" +
                "   - sdk_versions (SDK versions or images)\n" +
                "   - runtime_versions (runtime/container versions if distinct)\n" +
                "   - target_frameworks (all TFMs including multi-targeting)\n" +
                "4. Output ONLY the JSON object EXACTLY matching the schema. No commentary, no markdown, no code fences.\n" +
                "If you have not yet read necessary files, use additional tool calls before producing the final JSON.\n" +
                "Return ONLY the final JSON once you are certain.")
        };

        ChatOptions options = new ChatOptions
        {
            Tools = _toolDescriptors.ToList()
        };

        ChatResponse response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);

        string raw = GetAssistantText(response);
        string json = ExtractJson(raw);

        if (!IsValidJson(json))
        {
            _logger.LogWarning("Model response not valid JSON. Raw: {Raw}", raw);
            throw new InvalidOperationException("Model did not return valid JSON. Refine prompt or verify tool availability.");
        }

        return json;
    }

    private static string GetAssistantText(ChatResponse response)
    {
        if (response == null)
        {
            return string.Empty;
        }

        if (response.Messages != null && response.Messages.Count > 0)
        {
            // Take last assistant message with any text
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

    private static string ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');

        if (start >= 0 && end > start)
        {
            return text.Substring(start, end - start + 1).Trim();
        }

        return text.Trim();
    }

    private static bool IsValidJson(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            using (JsonDocument doc = JsonDocument.Parse(candidate))
            {
                return true;
            }
        }
        catch
        {
            return false;
        }
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

            _logger.LogWarning("System prompt file not found at {Path}. Falling back to default prompt.", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load system prompt file.");
        }

        return "You are an AI agent. (Default prompt fallback)";
    }
}
