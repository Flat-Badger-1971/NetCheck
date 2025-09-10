using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NetCheck.AI.Mcp;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace NetCheck.Services;

public class AIEngine : IAIEngine
{
    private readonly IChatClient _chatClient;
    private readonly IOllamaModelService _ollamaService;
    private readonly ILogger<AIEngine> _logger;
    private readonly IList<AITool> _toolDescriptors;
    private readonly IReadOnlyDictionary<string, IMcpInvokableTool> _invokers;

    private readonly List<ChatMessage> _systemMessages;

    private const int MaxPlannerIterations = 30;
    private const int MaxMalformed = 6;
    private const int MaxEvidenceBytes = 200_000;
    private const int MaxFileContentBytesPerFile = 8000;

    private static readonly Regex s_jsonObjectRegex = new(@"\{(?:[^{}""']|""(?:\\.|[^""])*""|'(?:\\.|[^'])*')*?\}", RegexOptions.Singleline | RegexOptions.Compiled);

    public AIEngine(
        IChatClient chatClient,
        IOllamaModelService ollamaService,
        ILogger<AIEngine> logger,
        IList<AITool> toolDescriptors,
        IReadOnlyDictionary<string, IMcpInvokableTool> invokers)
    {
        _chatClient = chatClient;
        _ollamaService = ollamaService;
        _logger = logger;
        _toolDescriptors = toolDescriptors;
        _invokers = invokers;

        _systemMessages =
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

        if (!await _ollamaService.IsModelAvailableAsync() &&
            !await _ollamaService.EnsureModelIsLoadedAsync())
        {
            _logger.LogError("Model unavailable for repository scan.");
            throw new InvalidOperationException("Model not available.");
        }

        string repoNormalized = Uri.UnescapeDataString(repository);

        // Build tool catalog for explicit guidance
        StringBuilder toolList = new();
        foreach (AITool t in _toolDescriptors)
        {
            toolList.Append("- ").Append(t.Name).AppendLine();
        }

        List<ChatMessage> conversation = new(_systemMessages)
        {
            new ChatMessage(
                ChatRole.User,
                "TARGET REPOSITORY: " + repoNormalized + "\n" +
                "You MUST discover .NET versions ONLY by invoking the available tools. NO GUESSING. NO PROSE.\n" +
                "AVAILABLE TOOLS:\n" + toolList +
                "PROTOCOL (STRICT - one JSON object per turn, nothing else):\n" +
                "Tool call example:\n" +
                "{\"action\":\"call_tool\",\"tool\":\"<toolName>\",\"arguments\":{ },\"reason\":\"why this tool is needed\"}\n" +
                "Final result signal example:\n" +
                "{\"action\":\"final_result\"}\n" +
                "You MUST read (if they exist): global.json, every *.csproj, Directory.Build.props / .targets, Dockerfile*, *.yml / *.yaml.\n" +
                "DO NOT produce the final schema JSON. Only signal final_result first; I will then give you all evidence to produce the JSON in a second pass.\n" +
                "RULES:\n" +
                "- No markdown\n" +
                "- No explanations outside JSON\n" +
                "- Always include 'action'\n" +
                "- Use 'arguments': {} if none\n" +
                "Start by listing the repository root with an appropriate tool call.\n")
        };

        Dictionary<string, string> fileContents = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> listedPaths = new(StringComparer.OrdinalIgnoreCase);

        ChatOptions options = new() { Tools = _toolDescriptors };

        int malformed = 0;
        int consecutiveParseErrors = 0;

        for (int iter = 1; iter <= MaxPlannerIterations; iter++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ChatResponse response = await _chatClient.GetResponseAsync(conversation, options, cancellationToken);
            string assistant = ExtractAssistantText(response);

            string? planJson = ExtractSingleJsonObject(assistant);
            if (planJson == null)
            {
                malformed++;
                consecutiveParseErrors++;
                _logger.LogWarning("Planner iteration {Iter}: no JSON found.", iter);
                if (InsertProtocolReminder(conversation, consecutiveParseErrors)) continue;
                if (malformed > MaxMalformed) throw new InvalidOperationException("Too many malformed planner responses.");
                conversation.Add(new ChatMessage(ChatRole.User, "Malformed. Provide only a JSON object with 'action'.")); 
                continue;
            }

            if (!TryParsePlan(planJson, out PlannerAction? plan, out string? error))
            {
                malformed++;
                consecutiveParseErrors++;
                _logger.LogWarning("Planner iteration {Iter}: parse error {Err}. Raw={Raw}",
                    iter, error, Truncate(planJson));
                if (InsertProtocolReminder(conversation, consecutiveParseErrors)) continue;
                if (malformed > MaxMalformed) throw new InvalidOperationException("Too many invalid planner responses.");
                conversation.Add(new ChatMessage(ChatRole.User, "Invalid plan JSON. Use either call_tool or final_result JSON object ONLY."));
                continue;
            }

            consecutiveParseErrors = 0;
            malformed = 0;

            if (plan!.Action == PlannerActionType.CallTool)
            {
                if (string.IsNullOrWhiteSpace(plan.Tool))
                {
                    conversation.Add(new ChatMessage(ChatRole.User, "Missing tool name. Provide tool property."));
                    continue;
                }

                if (!_invokers.TryGetValue(plan.Tool, out IMcpInvokableTool invoker))
                {
                    conversation.Add(new ChatMessage(ChatRole.User, $"Tool '{plan.Tool}' not available. Choose from the listed tools."));
                    continue;
                }

                object args = plan.Arguments ?? new { };
                object toolResult;
                try
                {
                    toolResult = await invoker.InvokeAsync(args, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Tool invocation failed ({Tool})", plan.Tool);
                    toolResult = new { tool = plan.Tool, error = true, message = ex.Message };
                }

                HarvestEvidence(toolResult, fileContents, listedPaths);

                string toolJson = SafeSerialize(toolResult);
                conversation.Add(new ChatMessage(ChatRole.Assistant, toolJson));
                conversation.Add(new ChatMessage(ChatRole.User, "Continue planning. If all needed files have been read respond with {\"action\":\"final_result\"}."));
                continue;
            }

            if (plan.Action == PlannerActionType.FinalResult)
            {
                if (!HasAuthoritativeFile(fileContents.Keys))
                {
                    conversation.Add(new ChatMessage(ChatRole.User, "Authoritative files not yet read (need at least one project or global.json). Continue with tool calls."));
                    continue;
                }

                string evidenceSummary = BuildEvidenceSummary(fileContents);
                string finalJson = await ProduceFinalJson(repoNormalized, evidenceSummary, cancellationToken);
                return finalJson;
            }

            conversation.Add(new ChatMessage(ChatRole.User, "Unknown action. Use call_tool or final_result only."));
        }

        throw new InvalidOperationException("Exceeded maximum planner iterations without final_result.");
    }

    #region Second Pass Final JSON
    private async Task<string> ProduceFinalJson(string repository, string evidenceSummary, CancellationToken ct)
    {
        List<ChatMessage> convo = new(_systemMessages)
        {
            new ChatMessage(ChatRole.User,
                "EVIDENCE:\n" + evidenceSummary + "\n" +
                "Using ONLY this evidence (no guessing), output final JSON:\n" +
                "{ \"repository\":\"" + repository + "\", \"dotnet_versions\":{\"sdk_versions\":[],\"runtime_versions\":[],\"target_frameworks\":[]}, \"scan_timestamp\":\"<UTC ISO 8601>\" }\n" +
                "Rules: Only include values explicitly present in evidence. If none, empty array. No markdown, just JSON.")
        };

        ChatResponse response = await _chatClient.GetResponseAsync(convo, new ChatOptions(), ct);
        string assistant = ExtractAssistantText(response);

        string? json = ExtractSingleJsonObject(assistant);
        if (json == null || !IsValidJson(json))
        {
            _logger.LogWarning("Final JSON not valid. Raw: {Raw}", Truncate(assistant));
            throw new InvalidOperationException("Model failed to produce valid final JSON.");
        }

        return ForceRepositoryAndTimestamp(json, repository);
    }

    private static string ForceRepositoryAndTimestamp(string json, string repository)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            using MemoryStream ms = new();
            using Utf8JsonWriter w = new(ms, new JsonWriterOptions { Indented = true });

            w.WriteStartObject();
            w.WriteString("repository", repository);

            if (root.TryGetProperty("dotnet_versions", out JsonElement dv) && dv.ValueKind == JsonValueKind.Object)
            {
                w.WritePropertyName("dotnet_versions");
                w.WriteStartObject();
                CopyStringArray(w, dv, "sdk_versions");
                CopyStringArray(w, dv, "runtime_versions");
                CopyStringArray(w, dv, "target_frameworks");
                w.WriteEndObject();
            }
            else
            {
                w.WritePropertyName("dotnet_versions");
                w.WriteStartObject();
                w.WritePropertyName("sdk_versions"); w.WriteStartArray(); w.WriteEndArray();
                w.WritePropertyName("runtime_versions"); w.WriteStartArray(); w.WriteEndArray();
                w.WritePropertyName("target_frameworks"); w.WriteStartArray(); w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteString("scan_timestamp", DateTimeOffset.UtcNow.ToString("o"));
            w.WriteEndObject();
            w.Flush();
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return json;
        }
    }

    private static void CopyStringArray(Utf8JsonWriter w, JsonElement parent, string name)
    {
        w.WritePropertyName(name);
        if (parent.TryGetProperty(name, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
        {
            HashSet<string> dedup = new(StringComparer.OrdinalIgnoreCase);
            w.WriteStartArray();
            foreach (JsonElement e in arr.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.String)
                {
                    string? v = e.GetString();
                    if (!string.IsNullOrWhiteSpace(v) && dedup.Add(v.Trim()))
                    {
                        w.WriteStringValue(v.Trim());
                    }
                }
            }
            w.WriteEndArray();
        }
        else
        {
            w.WriteStartArray(); w.WriteEndArray();
        }
    }
    #endregion

    #region Planner Parsing / Utilities
    private enum PlannerActionType { Unknown, CallTool, FinalResult }
    private sealed class PlannerAction
    {
        public PlannerActionType Action { get; set; }
        public string? Tool { get; set; }
        public object? Arguments { get; set; }
    }

    private static bool TryParsePlan(string json, out PlannerAction? plan, out string? error)
    {
        plan = null; error = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            // Auto unwrap if nested object { "request": { ... } }
            if (root.GetRawText().Length < 5)
            {
                error = "Too small";
                return false;
            }

            // If there's a single property whose value is an object with potential action signature, unwrap
            if (root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Count() == 1)
            {
                var sole = root.EnumerateObject().First();
                if (sole.Value.ValueKind == JsonValueKind.Object &&
                    (sole.Value.TryGetProperty("tool", out _) ||
                     sole.Value.TryGetProperty("action", out _) ||
                     sole.Value.TryGetProperty("final_result", out _)))
                {
                    root = sole.Value;
                }
            }

            PlannerAction p = new();

            // Try direct action
            if (root.TryGetProperty("action", out JsonElement act) && act.ValueKind == JsonValueKind.String)
            {
                string? a = act.GetString();
                p.Action = a?.ToLowerInvariant() switch
                {
                    "call_tool" => PlannerActionType.CallTool,
                    "final_result" => PlannerActionType.FinalResult,
                    _ => PlannerActionType.Unknown
                };
            }
            else
            {
                // Heuristics: if 'tool' exists but no 'action', assume call_tool
                if (root.TryGetProperty("tool", out _))
                {
                    p.Action = PlannerActionType.CallTool;
                }
                // Heuristics: if key like final_result / done / complete
                else if (root.TryGetProperty("final_result", out _) ||
                         root.TryGetProperty("done", out _) ||
                         root.TryGetProperty("complete", out _))
                {
                    p.Action = PlannerActionType.FinalResult;
                }
                else
                {
                    error = "Missing action";
                    return false;
                }
            }

            if (p.Action == PlannerActionType.CallTool)
            {
                if (root.TryGetProperty("tool", out JsonElement toolEl) && toolEl.ValueKind == JsonValueKind.String)
                    p.Tool = toolEl.GetString();
                if (root.TryGetProperty("arguments", out JsonElement argsEl))
                    p.Arguments = JsonToPlain(argsEl);
            }

            plan = p;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool InsertProtocolReminder(List<ChatMessage> conversation, int consecutiveParseErrors)
    {
        if (consecutiveParseErrors == 2 || consecutiveParseErrors == 4)
        {
            conversation.Add(new ChatMessage(ChatRole.User,
                "PROTOCOL REMINDER: Respond ONLY with a JSON object.\n" +
                "Valid examples:\n" +
                "{\"action\":\"call_tool\",\"tool\":\"<tool>\",\"arguments\":{},\"reason\":\"...\"}\n" +
                "{\"action\":\"final_result\"}\n" +
                "No extra text."));
            return true;
        }
        return false;
    }

    private static object? JsonToPlain(JsonElement el) =>
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

    private static string? ExtractSingleJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (Match m in s_jsonObjectRegex.Matches(raw))
        {
            string candidate = m.Value.Trim();
            if (IsValidJson(candidate)) return candidate;
        }
        return null;
    }

    private static bool IsValidJson(string candidate)
    {
        try { using JsonDocument _ = JsonDocument.Parse(candidate); return true; }
        catch { return false; }
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
    #endregion

    #region Evidence
    private static bool HasAuthoritativeFile(IEnumerable<string> files)
    {
        foreach (string f in files)
        {
            string lf = f.ToLowerInvariant();
            if (lf.EndsWith(".csproj") || lf.EndsWith(".fsproj") || lf.EndsWith(".vbproj") ||
                lf == "global.json" || lf.EndsWith("directory.build.props") || lf.EndsWith("directory.build.targets"))
            {
                return true;
            }
        }
        return false;
    }

    private void HarvestEvidence(object toolResult, Dictionary<string, string> fileContents, HashSet<string> listedPaths)
    {
        string json = SafeSerialize(toolResult);
        if (Encoding.UTF8.GetByteCount(json) > MaxEvidenceBytes) return;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            HarvestRecursive(doc.RootElement, fileContents, listedPaths);
        }
        catch
        {
        }
    }

    private void HarvestRecursive(JsonElement el, Dictionary<string, string> fileContents, HashSet<string> listedPaths)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                string? name = null;
                string? path = null;
                string? content = null;

                foreach (JsonProperty p in el.EnumerateObject())
                {
                    if (p.NameEquals("name") && p.Value.ValueKind == JsonValueKind.String) name = p.Value.GetString();
                    if ((p.NameEquals("path") || p.NameEquals("file") || p.NameEquals("fullPath")) && p.Value.ValueKind == JsonValueKind.String) path = p.Value.GetString();
                    if ((p.NameEquals("content") || p.NameEquals("text")) && p.Value.ValueKind == JsonValueKind.String) content = p.Value.GetString();
                    HarvestRecursive(p.Value, fileContents, listedPaths);
                }

                string chosen = path ?? name ?? "";
                if (!string.IsNullOrWhiteSpace(chosen))
                {
                    listedPaths.Add(chosen);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        string truncated = TruncateContent(content);
                        fileContents[chosen] = truncated;
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in el.EnumerateArray())
                    HarvestRecursive(item, fileContents, listedPaths);
                break;
        }
    }

    private static string TruncateContent(string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length <= MaxFileContentBytesPerFile) return content;
        string truncated = Encoding.UTF8.GetString(bytes, 0, MaxFileContentBytesPerFile);
        return truncated + "\n/*...truncated...*/";
    }

    private string BuildEvidenceSummary(Dictionary<string, string> fileContents)
    {
        StringBuilder sb = new();
        foreach (var kv in fileContents.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine("----- FILE: " + kv.Key);
            sb.AppendLine(kv.Value);
            sb.AppendLine();
        }
        return sb.ToString();
    }
    #endregion

    #region Helpers
    private static string SafeSerialize(object? obj)
    {
        try { return JsonSerializer.Serialize(obj); } catch { return "{}"; }
    }

    private static string Truncate(string s) => s.Length <= 160 ? s : s[..160] + "...";



    private string GetSystemPrompt()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Prompts", "system.txt");
            if (File.Exists(path))
                return File.ReadAllText(path);
            _logger.LogWarning("System prompt file not found at {Path}. Using fallback.", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load system prompt.");
        }
        return "You are an AI agent.";
    }
    #endregion
}
