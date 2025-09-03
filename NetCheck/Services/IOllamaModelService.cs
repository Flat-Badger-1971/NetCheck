using System.Threading.Tasks;

namespace NetCheck.Services
{
    public interface IOllamaModelService
    {
        Task<bool> EnsureModelIsLoadedAsync();
        Task<bool> IsModelAvailableAsync();
        Task<bool> PullModelAsync();
    }
}