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
    McpClient mcpClient,
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

        // this is here as ollama silently discards tools it can't accommodate due the number being supplied
        // it's also good practice to allow as few tools as possible to reduce the chances of the wrong tool being used
        IEnumerable<McpClientTool> mcpTools = (await mcpClient.ListToolsAsync())
            .Where(t => t.Name is "search_pull_requests" or "create_issue");

        Console.WriteLine("Available tools (MCP + local):");

        foreach (AITool tool in mcpTools)
        {
            Console.WriteLine($"{tool.Name} - {tool.Description}");
        }

#pragma warning disable S125 // commented out code
        // PHASE 1: Retrieval
        // local tools could be added here if needed
        // e.g.:
        // List<AITool> localTools = ParseTools.GetLocalTools();
        // Tools = [.. mcpTools, .. localTools],
#pragma warning restore S125
        ChatOptions allToolOptions = new()
        {
            Tools = [.. mcpTools],
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

        string retrievalText = await GetResponse(conversation, allToolOptions);
        conversation.Add(new ChatMessage(ChatRole.Assistant, retrievalText));

        if (!retrievalText.Contains($"repo:{RepoOwner}/{RepoName}", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Search result may not be scoped correctly. Raw retrieval text:\n{Text}", retrievalText);
        }

        // PHASE 2: Normalisation (PullRequests array)
        ChatOptions formattingOptions = new()
        {
            Tools = Array.Empty<AITool>(),
            ToolMode = ChatToolMode.Auto
        };

        userPrompt.Clear();
        userPrompt.AppendLine("FORMAT_PULL_REQUESTS_JSON");
        userPrompt.AppendLine("Produce EXACTLY ONE block:");
        userPrompt.AppendLine("<JSON>[ { \"PullRequestNumber\": <number>, \"Title\": \"<string>\", \"Body\": \"<string>\" } ... ]</JSON>");
        userPrompt.AppendLine("Rules:");
        userPrompt.AppendLine("1. NOTHING before or after the <JSON> block.");
        userPrompt.AppendLine("2. Must be a JSON array (possibly empty) of objects with exactly those three keys.");
        userPrompt.AppendLine("3. If no pull requests, output <JSON>[]</JSON>.");
        userPrompt.AppendLine("4. No commentary / markdown / reasoning.");
        userPrompt.AppendLine("5. Do not output {}.");
        conversation.Add(new ChatMessage(ChatRole.User, userPrompt.ToString()));

        string pullRequestsJson = await GetTaggedJsonArrayAsync(conversation, formattingOptions, "pull request normalisation");
        conversation.Add(new ChatMessage(ChatRole.Assistant, $"<JSON>{pullRequestsJson}</JSON>"));

        // PHASE 3: Title validation -> failure objects
        ChatOptions analysisOptions = new()
        {
            Tools = Array.Empty<AITool>(),
            ToolMode = ChatToolMode.Auto
        };

        userPrompt.Clear();
        userPrompt.AppendLine("TITLE_VALIDATION");
        userPrompt.AppendLine("From the previously normalised pull request JSON array, identify every pull request whose Title DOES NOT start with TB-<digits> (e.g. TB-1234).");
        userPrompt.AppendLine("Output ONLY failures as an array of objects inside EXACTLY ONE block:");
        userPrompt.AppendLine("<JSON>[ { \"PullRequestNumber\": <number>, \"Check\": \"TitlePattern\", \"Passed\": false, \"Reason\": \"Title does not start with TB-<digits>\" } ... ]</JSON>");
        userPrompt.AppendLine("Rules:");
        userPrompt.AppendLine("1. Only failures; if all pass output <JSON>[]</JSON>.");
        userPrompt.AppendLine("2. No Passed=true objects.");
        userPrompt.AppendLine("3. Keys exactly: PullRequestNumber, Check, Passed, Reason.");
        userPrompt.AppendLine("4. No commentary, no wrapping object, no {}.");
        userPrompt.AppendLine("5. Reason must be exactly: Title does not start with TB-<digits>.");
        conversation.Add(new ChatMessage(ChatRole.User, userPrompt.ToString()));

        string titleFailuresJson = await GetTaggedJsonArrayAsync(conversation, analysisOptions, "title validation");
        conversation.Add(new ChatMessage(ChatRole.Assistant, $"<JSON>{titleFailuresJson}</JSON>"));

        // PHASE 4: Body hyperlink validation (failure objects)
        userPrompt.Clear();
        userPrompt.AppendLine("BODY_HYPERLINK_VALIDATION");
        userPrompt.AppendLine("Using the normalised pull request list:");
        userPrompt.AppendLine("A. If a Title does NOT start with TB-<digits>, treat it as a failure: Reason = \"No TB-<digits> ticket in Title\".");
        userPrompt.AppendLine("B. If a Title DOES start with TB-<digits>, extract that ticket (e.g. TB-1234). The Body MUST contain an http/https URL that includes the exact ticket substring. If absent: Reason = \"Missing hyperlink containing ticket\".");
        userPrompt.AppendLine("Output ONLY failures as an array of objects inside EXACTLY ONE block:");
        userPrompt.AppendLine("<JSON>[ { \"PullRequestNumber\": <number>, \"Check\": \"BodyHyperlink\", \"Passed\": false, \"Reason\": \"<reason>\" } ... ]</JSON>");
        userPrompt.AppendLine("Rules:");
        userPrompt.AppendLine("1. Only failures; if none output <JSON>[]</JSON>.");
        userPrompt.AppendLine("2. Keys exactly: PullRequestNumber, Check, Passed, Reason.");
        userPrompt.AppendLine("3. Allowed reasons: \"No TB-<digits> ticket in Title\" OR \"Missing hyperlink containing ticket\".");
        userPrompt.AppendLine("4. No commentary, no wrapping object, no {}.");
        conversation.Add(new ChatMessage(ChatRole.User, userPrompt.ToString()));

        string bodyFailuresJson = await GetTaggedJsonArrayAsync(conversation, analysisOptions, "body hyperlink validation");
        conversation.Add(new ChatMessage(ChatRole.Assistant, $"<JSON>{bodyFailuresJson}</JSON>"));

        // Token estimation
        int tokenEstimate = await TokenEstimator.EstimateLlamaConversationTokens(conversation);

        // PHASE 5: Create GitHub issue if any failures
        int titleFailCount = CountArrayItems(titleFailuresJson);
        int bodyFailCount  = CountArrayItems(bodyFailuresJson);

        if (titleFailCount == 0 && bodyFailCount == 0)
        {
            logger.LogInformation("No failures detected; skipping issue creation phase.");
            return string.Empty;
        }

        string failureIssueBody = BuildIssueBody(titleFailuresJson, bodyFailuresJson);

        userPrompt.Clear();
        userPrompt.AppendLine("CREATE_VALIDATION_FAILURE_ISSUE");
        userPrompt.AppendLine("Make exactly one create_issue tool call.");
        userPrompt.AppendLine("Use ONLY arguments: body, title and repo.");
        userPrompt.AppendLine($"repo={RepoOwner}/{RepoName}");
        userPrompt.AppendLine($"owner={RepoOwner}");
        userPrompt.AppendLine("title=PR Checks Failed");
        userPrompt.AppendLine("The body argument must include everything between the following tags - <<BODY_START>> <<BODY_END>> exactly as defined (the same as using @ before a string in c#)");
        userPrompt.AppendLine($"<<BODY_START>>{failureIssueBody}<<BODY_END>>");
        userPrompt.AppendLine($"Do NOT include the <<BODY_START>> or <<BODY_END>> tags in the actual body argument.");
        userPrompt.AppendLine("The body MUST NOT contain any HTML elements or anything that could be construed as HTML.");
        userPrompt.AppendLine("Do NOT call any tool more than once. Do not format or summarise.");

        conversation.Add(new ChatMessage(ChatRole.User, userPrompt.ToString()));

        string issueResponse = await GetResponse(conversation, allToolOptions);
        conversation.Add(new ChatMessage(ChatRole.Assistant, issueResponse));

        // PRETTY consolidated JSON (set pretty:false for compact)
        string consolidated = BuildConsolidatedJson(
            pullRequestsJson,
            titleFailuresJson,
            bodyFailuresJson,
            issueResponse,
            tokenEstimate,
            pretty: true);

        return consolidated;
    }

    private static string BuildIssueBody(string titleFailuresJson, string bodyFailuresJson)
    {
        StringBuilder sb = new StringBuilder("**Automated PR checks detected failures. Please address the following issues:**");

        AppendFailures("Title Pattern Failures", titleFailuresJson, sb);
        AppendFailures("Body Hyperlink Failures", bodyFailuresJson, sb);

        sb.AppendLine("Please update affected pull requests to satisfy validation rules.");

        return sb.ToString().TrimEnd();
    }

    private static void AppendFailures(string header, string json, StringBuilder sb)
    {
        int count = CountArrayItems(json);

        if (count == 0)
        {
            return;
        }

        sb.AppendLine($"## {header} ({count})");

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                int pr = el.GetProperty("PullRequestNumber").GetInt32();
                string reason = el.GetProperty("Reason").GetString() ?? "(no reason)";
                sb.AppendLine($"- PR #{pr}: {reason}");
            }
        }
        catch
        {
            sb.AppendLine("(Failed to parse failure details)");
        }
    }

    private string BuildConsolidatedJson(
    string pullRequests,
    string titleFailures,
    string bodyFailures,
    string issueResponse,
    int tokens,
    bool pretty = true)
{
    pullRequests = string.IsNullOrWhiteSpace(pullRequests) ? "[]" : pullRequests.Trim();
    titleFailures = string.IsNullOrWhiteSpace(titleFailures) ? "[]" : titleFailures.Trim();
    bodyFailures = string.IsNullOrWhiteSpace(bodyFailures) ? "[]" : bodyFailures.Trim();
    issueResponse = string.IsNullOrWhiteSpace(issueResponse) ? "\"empty\"" : issueResponse.Trim();

    try
    {
        using (MemoryStream ms = new MemoryStream())
        {
            using (Utf8JsonWriter writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = pretty }))
            {
                writer.WriteStartObject();

                writer.WritePropertyName("Repository");
                writer.WriteStartObject();
                writer.WriteString("Owner", RepoOwner);
                writer.WriteString("Name", RepoName);
                writer.WriteEndObject();

                writer.WritePropertyName("PullRequests");

                using (JsonDocument prDoc = JsonDocument.Parse(pullRequests))
                {
                    prDoc.RootElement.WriteTo(writer);
                }

                writer.WritePropertyName("TitlePatternFailures");

                using (JsonDocument titleDoc = JsonDocument.Parse(titleFailures))
                {
                    titleDoc.RootElement.WriteTo(writer);
                }

                writer.WritePropertyName("BodyHyperlinkFailures");

                using (JsonDocument bodyDoc = JsonDocument.Parse(bodyFailures))
                {
                    bodyDoc.RootElement.WriteTo(writer);
                }

                writer.WritePropertyName("IssueCreationResponse");
                writer.WriteStartObject();
                writer.WriteString("IssueInformation", issueResponse);
                writer.WriteEndObject();

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

    private static int CountArrayItems(string jsonArray)
    {
        if (string.IsNullOrWhiteSpace(jsonArray))
        {
            return 0;
        }

        try
        {
            using (JsonDocument doc = JsonDocument.Parse(jsonArray))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return doc.RootElement.GetArrayLength();
                }
            }
        }
        catch
        {
            // ignore
        }

        return 0;
    }

    private static string EscapeJson(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

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

    private static bool IsJsonArray(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            using (JsonDocument doc = JsonDocument.Parse(candidate))
            {
                return doc.RootElement.ValueKind == JsonValueKind.Array;
            }
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

#pragma warning disable S3267
        await foreach (ChatResponseUpdate update in chatClient.GetStreamingResponseAsync(conversation, opts))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                sb.Append(update.Text);
                Console.Write(update.Text);
            }
        }
#pragma warning restore S3267
        logger.LogDebug("\nCompleted streaming response");

        return sb.ToString().Trim();
    }

    private static string SafeSerialiseArgs(IReadOnlyDictionary<string, object?>? args)
    {
        if (args == null) return "<null>";
        try
        {
            return JsonSerializer.Serialize(args);
        }
        catch
        {
            return "<unserializable>";
        }
    }

    private static string ExtractTaggedJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        Match match = JsonTagRegex.Match(raw);

        if (!match.Success)
        {
            return null;
        }

        string candidate = match.Groups[1].Value.Trim();

        return IsJsonArray(candidate) ? candidate : null;
    }

    private static string ExtractJsonArrayFallback(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

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
