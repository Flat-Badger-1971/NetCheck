using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using NetCheck.Utility;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace NetCheck.Services;

public class AIEngine(
    IChatClient chatClient,
    IMcpClient mcpClient,
    ILogger<AIEngine> logger) : IAIEngine
{
    #region secret stuff
    private const string RepoName = "NetCheck";
    private const string RepoOwner = "Flat-Badger-1971";
    #endregion

    private static readonly Regex JsonTagRegex = new(@"<JSON>\s*(\[[\s\S]*?])\s*</JSON>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const int MaxTaggedArrayAttempts = 3;

    public async Task<string> RunAgent()
    {
        List<ChatMessage> conversation =
        [
            new(ChatRole.System, GetSystemPrompt())
        ];

        IEnumerable<McpClientTool> mcpTools = (await mcpClient.ListToolsAsync(null, default))
            .Where(t => t.Name is "search_pull_requests" or "update_pull_request");

        Console.WriteLine("Available tools (MCP + local):");
        foreach (AITool tool in mcpTools)
        {
            Console.WriteLine($"{tool.Name} - {tool.Description}");
        }

        // PHASE 1: Retrieval
        ChatOptions retrievalOptions = new()
        {
            Tools = [.. mcpTools.Where(t => t.Name == "search_pull_requests")],
            AllowMultipleToolCalls = false,
            ToolMode = ChatToolMode.RequireAny
        };

        StringBuilder userPrompt = new();
        userPrompt.AppendLine($"Repository owner: {RepoOwner}");
        userPrompt.AppendLine($"Repository name: {RepoName}");
        userPrompt.AppendLine("Make exactly one search_pull_requests tool call.");
        userPrompt.AppendLine("Use arguments: owner, repo and query.");
        userPrompt.AppendLine($"query MUST be: repo:{RepoOwner}/{RepoName} is:pr is:open");
        userPrompt.AppendLine("Do NOT call any tool more than once. Do not format or summarise.");
        conversation.Add(new ChatMessage(ChatRole.User, userPrompt.ToString()));

        string retrievalText = await GetResponse(conversation, retrievalOptions);
        conversation.Add(new ChatMessage(ChatRole.Assistant, retrievalText));

        if (!retrievalText.Contains($"repo:{RepoOwner}/{RepoName}", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Search result may not be scoped correctly. Raw retrieval text:\n{Text}", retrievalText);
        }

        // PHASE 2: Normalization (PullRequests array)
        ChatOptions formattingOptions = new()
        {
            Tools = Array.Empty<AITool>(),
            ToolMode = ChatToolMode.Auto
        };

        conversation.Add(new ChatMessage(ChatRole.User,
            "FORMAT_PULL_REQUESTS_JSON\n" +
            "Produce EXACTLY ONE block:\n" +
            "<JSON>[ { \"PullRequestNumber\": <number>, \"Title\": \"<string>\", \"Body\": \"<string>\" } ... ]</JSON>\n" +
            "Rules:\n" +
            "1. NOTHING before or after the <JSON> block.\n" +
            "2. Must be a JSON array (possibly empty) of objects with exactly those three keys.\n" +
            "3. If no pull requests, output <JSON>[]</JSON>.\n" +
            "4. No commentary / markdown / reasoning.\n" +
            "5. Do not output {}."
        ));

        string pullRequestsJson = await GetTaggedJsonArrayAsync(conversation, formattingOptions, "pull request normalization");
        conversation.Add(new ChatMessage(ChatRole.Assistant, $"<JSON>{pullRequestsJson}</JSON>"));

        // PHASE 3: Title validation -> failure objects
        ChatOptions analysisOptions = new()
        {
            Tools = Array.Empty<AITool>(),
            ToolMode = ChatToolMode.Auto
        };

        conversation.Add(new ChatMessage(ChatRole.User,
            "TITLE_VALIDATION\n" +
            "From the previously normalized pull request JSON array, identify every pull request whose Title DOES NOT start with TB-<digits> (e.g. TB-1234).\n" +
            "Output ONLY failures as an array of objects inside EXACTLY ONE block:\n" +
            "<JSON>[ { \"PullRequestNumber\": <number>, \"Check\": \"TitlePattern\", \"Passed\": false, \"Reason\": \"Title does not start with TB-<digits>\" } ... ]</JSON>\n" +
            "Rules:\n" +
            "1. Only failures; if all pass output <JSON>[]</JSON>.\n" +
            "2. No Passed=true objects.\n" +
            "3. Keys exactly: PullRequestNumber, Check, Passed, Reason.\n" +
            "4. No commentary, no wrapping object, no {}.\n" +
            "5. Reason must be exactly: Title does not start with TB-<digits>."
        ));

        string titleFailuresJson = await GetTaggedJsonArrayAsync(conversation, analysisOptions, "title validation");
        conversation.Add(new ChatMessage(ChatRole.Assistant, $"<JSON>{titleFailuresJson}</JSON>"));

        // PHASE 4: Body hyperlink validation (failure objects)
        conversation.Add(new ChatMessage(ChatRole.User,
            "BODY_HYPERLINK_VALIDATION\n" +
            "Using the normalized pull request list:\n" +
            "A. If a Title does NOT start with TB-<digits>, treat it as a failure: Reason = \"No TB-<digits> ticket in Title\".\n" +
            "B. If a Title DOES start with TB-<digits>, extract that ticket (e.g. TB-1234). The Body MUST contain an http/https URL that includes the exact ticket substring. If absent: Reason = \"Missing hyperlink containing ticket\".\n" +
            "Output ONLY failures as an array of objects inside EXACTLY ONE block:\n" +
            "<JSON>[ { \"PullRequestNumber\": <number>, \"Check\": \"BodyHyperlink\", \"Passed\": false, \"Reason\": \"<reason>\" } ... ]</JSON>\n" +
            "Rules:\n" +
            "1. Only failures; if none output <JSON>[]</JSON>.\n" +
            "2. Keys exactly: PullRequestNumber, Check, Passed, Reason.\n" +
            "3. Allowed reasons: \"No TB-<digits> ticket in Title\" OR \"Missing hyperlink containing ticket\".\n" +
            "4. No commentary, no wrapping object, no {}."
        ));

        string bodyFailuresJson = await GetTaggedJsonArrayAsync(conversation, analysisOptions, "body hyperlink validation");
        conversation.Add(new ChatMessage(ChatRole.Assistant, $"<JSON>{bodyFailuresJson}</JSON>"));

        // Token estimation
        int tokenEstimate = await TokenEstimator.EstimateLlamaConversationTokens(conversation);

        // PRETTY consolidated JSON (set pretty:false for compact)
        string consolidated = BuildConsolidatedJson(
            pullRequestsJson,
            titleFailuresJson,
            bodyFailuresJson,
            tokenEstimate,
            pretty: true);

        return consolidated;
    }

    private string BuildConsolidatedJson(
        string pullRequests,
        string titleFailures,
        string bodyFailures,
        int tokens,
        bool pretty = true)
    {
        pullRequests   = string.IsNullOrWhiteSpace(pullRequests)   ? "[]" : pullRequests.Trim();
        titleFailures  = string.IsNullOrWhiteSpace(titleFailures)  ? "[]" : titleFailures.Trim();
        bodyFailures   = string.IsNullOrWhiteSpace(bodyFailures)   ? "[]" : bodyFailures.Trim();

        try
        {
            using JsonDocument prDoc    = JsonDocument.Parse(pullRequests);
            using JsonDocument titleDoc = JsonDocument.Parse(titleFailures);
            using JsonDocument bodyDoc  = JsonDocument.Parse(bodyFailures);

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = pretty }))
            {
                writer.WriteStartObject();

                writer.WritePropertyName("Repository");
                writer.WriteStartObject();
                writer.WriteString("Owner", RepoOwner);
                writer.WriteString("Name", RepoName);
                writer.WriteEndObject();

                writer.WritePropertyName("PullRequests");
                prDoc.RootElement.WriteTo(writer);

                writer.WritePropertyName("TitlePatternFailures");
                titleDoc.RootElement.WriteTo(writer);

                writer.WritePropertyName("BodyHyperlinkFailures");
                bodyDoc.RootElement.WriteTo(writer);

                writer.WritePropertyName("Stats");
                writer.WriteStartObject();
                writer.WriteNumber("TokenEstimate", tokens);

                writer.WritePropertyName("PhaseFailures");
                writer.WriteStartObject();
                writer.WriteNumber("TitlePattern", CountArrayItems(titleFailures));
                writer.WriteNumber("BodyHyperlink", CountArrayItems(bodyFailures));
                writer.WriteEndObject(); // PhaseFailures

                writer.WriteEndObject(); // Stats

                writer.WriteEndObject(); // root
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to pretty-build JSON; falling back to minimal JSON.");

            // Fallback minimal (compact) JSON (corrected braces)
            return $"{{\"Repository\":{{\"Owner\":\"{EscapeJson(RepoOwner)}\",\"Name\":\"{EscapeJson(RepoName)}\"}}," +
                   $"\"PullRequests\":{pullRequests}," +
                   $"\"TitlePatternFailures\":{titleFailures}," +
                   $"\"BodyHyperlinkFailures\":{bodyFailures}," +
                   $"\"Stats\":{{\"TokenEstimate\":{tokens},\"PhaseFailures\":{{\"TitlePattern\":{CountArrayItems(titleFailures)},\"BodyHyperlink\":{CountArrayItems(bodyFailures)}}}}}}}";
        }
    }

    private int CountArrayItems(string jsonArray)
    {
        if (string.IsNullOrWhiteSpace(jsonArray)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(jsonArray);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return doc.RootElement.GetArrayLength();
            }
        }
        catch
        {
            // ignore
        }
        return 0;
    }

    private string EscapeJson(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private async Task<string> GetTaggedJsonArrayAsync(
        List<ChatMessage> conversation,
        ChatOptions opts,
        string purpose)
    {
        for (int attempt = 1; attempt <= MaxTaggedArrayAttempts; attempt++)
        {
            string raw = await GetResponse(conversation, opts);
            string extracted = ExtractTaggedJson(raw) ?? ExtractJsonArrayFallback(raw);

            if (IsJsonArray(extracted) && extracted != "{}")
            {
                return extracted;
            }

            conversation.Add(new ChatMessage(ChatRole.User,
                $"RETRY_{purpose.ToUpperInvariant()}_{attempt}\n" +
                "Your previous output was invalid. Respond again with exactly one <JSON>[ ... ]</JSON> block containing ONLY a valid JSON array (possibly empty). No other text. No empty {} object."));
        }

        logger.LogWarning("Max attempts reached for {Purpose}; returning empty array.", purpose);
        return "[]";
    }

    private bool IsJsonArray(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(candidate);
            return doc.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> GetResponse(List<ChatMessage> conversation, ChatOptions opts)
    {
        logger.LogDebug("Starting streaming response");
        StringBuilder sb = new();
        await foreach (ChatResponseUpdate update in chatClient.GetStreamingResponseAsync(conversation, opts))
        {
            sb.Append(update.Text);
            Console.Write(update.Text);
        }
        logger.LogDebug("\nCompleted streaming response");
        return sb.ToString().Trim();
    }

    private string ExtractTaggedJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var match = JsonTagRegex.Match(raw);
        if (!match.Success) return null;
        string candidate = match.Groups[1].Value.Trim();
        return IsJsonArray(candidate) ? candidate : null;
    }

    private string ExtractJsonArrayFallback(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        int s = raw.IndexOf('[');
        int e = raw.LastIndexOf(']');
        if (s >= 0 && e > s)
        {
            string slice = raw[s..(e + 1)];
            return IsJsonArray(slice) ? slice : null;
        }
        return null;
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
