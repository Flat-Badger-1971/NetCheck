using System.Threading;
using System.Threading.Tasks;

namespace NetCheck.AI.Mcp;

public interface IMcpInvokableTool
{
    string Name { get; }
    Task<object> InvokeAsync(object? input, CancellationToken cancellationToken = default);
}

