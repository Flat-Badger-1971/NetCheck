using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace NetCheck.AI.Mcp;


public static class McpChatToolFactory
{
    public static async Task<(IList<AITool> tools, IDictionary<string, IMcpInvokableTool> invokers)>
        CreateAsync(IMcpClient mcpClient, ILogger logger, CancellationToken cancellationToken = default)
    {
        IList<McpClientTool> remoteTools = await mcpClient.ListToolsAsync(null, cancellationToken);

        List<AITool> toolDescriptors = new();
        Dictionary<string, IMcpInvokableTool> invokers = new(StringComparer.OrdinalIgnoreCase);

        foreach (McpClientTool t in remoteTools)
        {
            LoggingMcpTool wrapper = new(t, mcpClient, logger);
            toolDescriptors.Add(wrapper);          // Exposed to model
            invokers[wrapper.Name] = wrapper;      // For manual orchestration if needed
        }

        return (toolDescriptors, invokers);
    }

    private sealed class LoggingMcpTool : AITool, IMcpInvokableTool
    {
        private readonly McpClientTool _tool;
        private readonly IMcpClient _client;
        private readonly ILogger _logger;

        public LoggingMcpTool(McpClientTool tool, IMcpClient client, ILogger logger)
        {
            _tool = tool;
            _client = client;
            _logger = logger;
        }

        public new string Name => _tool.Name;

        // Invocation method used manually (or by future planner). The Microsoft.Extensions.AI function invocation
        // pipeline may not call this automatically in your current package version, but you can invoke it if you add
        // a manual planning loop later.
        public async Task<object> InvokeAsync(object? input, CancellationToken cancellationToken = default)
        {
            Dictionary<string, object?> args = Normalize(input);
            _logger.LogInformation("tool-start {Tool} args={Args}", Name, SafeSerialize(args));
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                CallToolResult result = await _client.CallToolAsync(Name, args, null, null, cancellationToken);
                sw.Stop();
                object payload = new
                {
                    tool = Name,
                    result.IsError,
                    result.StructuredContent
                };
                _logger.LogInformation("tool-end   {Tool} ms={Elapsed} error={Err}", Name, sw.ElapsedMilliseconds, result.IsError);
                return payload;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "tool-fail  {Tool} ms={Elapsed}", Name, sw.ElapsedMilliseconds);
                return new { tool = Name, error = true, message = ex.Message };
            }
        }

        private static Dictionary<string, object?> Normalize(object? input)
        {
            if (input == null) return new();
            if (input is Dictionary<string, object?> d1) return d1;
            if (input is Dictionary<string, object> d2)
            {
                Dictionary<string, object?> conv = new();
                foreach (KeyValuePair<string, object> kv in d2) conv[kv.Key] = kv.Value;
                return conv;
            }
            if (input is JsonElement je) return FromJsonElement(je);
            try
            {
                string json = JsonSerializer.Serialize(input);
                return JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new();
            }
            catch
            {
                return new();
            }
        }

        private static Dictionary<string, object?> FromJsonElement(JsonElement element)
        {
            Dictionary<string, object?> dict = new();
            if (element.ValueKind != JsonValueKind.Object) return dict;
            foreach (JsonProperty prop in element.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt64(out long l) ? l : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Object => FromJsonElement(prop.Value),
                    JsonValueKind.Array => prop.Value.ToString(),
                    _ => null
                };
            }
            return dict;
        }

        private static string SafeSerialize(object? obj)
        {
            try { return JsonSerializer.Serialize(obj); } catch { return "<unserializable>"; }
        }
    }
}
