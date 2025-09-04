using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace NetCheck.AI.Mcp;

public static class McpChatToolFactory
{
    public static async Task<(IList<AITool> tools, IDictionary<string, IMcpInvokableTool> invokers)>
        CreateAsync(IMcpClient mcpClient, CancellationToken cancellationToken = default)
    {
        IList<McpClientTool> remoteTools = await mcpClient.ListToolsAsync(null, cancellationToken);

        List<AITool> aiTools = new();
        Dictionary<string, IMcpInvokableTool> invokers = new();

        foreach (McpClientTool t in remoteTools)
        {
            McpToolInvoker invoker = new(mcpClient, t);
            // AITool in 9.8.0 is typically a plain descriptor object (Name / Description / InputSchema)
            // We construct it by inheriting, or you can replace with a helper if API provides a ctor pattern.
            aiTools.Add(invoker);          // invoker derives from AITool (descriptor role)
            invokers[invoker.Name] = invoker;
        }

        return (aiTools, invokers);
    }

    private sealed class McpToolInvoker : AITool, IMcpInvokableTool
    {
        private readonly IMcpClient _client;
        private readonly McpClientTool _tool;

        public McpToolInvoker(IMcpClient client, McpClientTool tool)
        {
            _client = client;
            _tool = tool;
        }

        public new string Name => _tool.Name;

        public async Task<object> InvokeAsync(object input, CancellationToken cancellationToken = default)
        {
            Dictionary<string, object> args = Normalize(input);
            CallToolResult result = await _client.CallToolAsync(_tool.Name, args, null, null, cancellationToken);
            return new
            {
                tool = _tool.Name,
                result.IsError,
                result.StructuredContent
            };
        }

        private static Dictionary<string, object> Normalize(object input)
        {
            if (input == null)
            {
                return [];
            }

            if (input is Dictionary<string, object> d1)
            {
                return d1;
            }

            if (input is Dictionary<string, object> d2)
            {
                Dictionary<string, object> conv = [];
                foreach (KeyValuePair<string, object> kv in d2)

                {
                    conv[kv.Key] = kv.Value;
                }

                return conv;
            }

            if (input is JsonElement je)
            {
                return FromJsonElement(je);
            }

            string json = JsonSerializer.Serialize(input);

            return JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? [];
        }

        private static Dictionary<string, object> FromJsonElement(JsonElement element)
        {
            Dictionary<string, object> dict = [];

            if (element.ValueKind != JsonValueKind.Object)
            {
                return dict;
            }

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
    }
}
