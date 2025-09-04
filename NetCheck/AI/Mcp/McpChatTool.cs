using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace NetCheck.AI.Mcp;


public class McpChatTool(IMcpClient client, McpClientTool tool) : AITool
{
    public async Task<object> InvokeAsync(object input, CancellationToken cancellationToken = default)
    {
        // Normalize input to JSON element/string dictionary
        Dictionary<string, object> args;
        switch (input)
        {
            case null:
                args = [];
                break;
            case JsonElement je:
                args = JsonElementToDictionary(je);
                break;
            case Dictionary<string, object> dictObj:
                args = dictObj;
                break;
            default:
                // Last resort: serialize & deserialize
                string json = JsonSerializer.Serialize(input);
                args = JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? [];
                break;
        }

        CallToolResult result = await client.CallToolAsync(tool.Name, args, null, null, cancellationToken);

        // Return a simplified object the model can consume
        return new
        {
            tool = tool.Name,
            result.IsError,
            result.StructuredContent
        };
    }

    private static Dictionary<string, object> JsonElementToDictionary(JsonElement element)
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
                JsonValueKind.Object => JsonElementToDictionary(prop.Value),
                JsonValueKind.Array => prop.Value.ToString(),
                _ => null
            };
        }
        return dict;
    }
}

