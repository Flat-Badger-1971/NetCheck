using System.Threading.Tasks;

namespace NetCheck.Services;

public interface IOllamaModelService
{
    public Task<bool> EnsureModelIsLoadedAsync();
    public Task<bool> IsModelAvailableAsync();
    public Task<bool> PullModelAsync();
}
