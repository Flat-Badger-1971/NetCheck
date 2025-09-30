using System.Threading.Tasks;

namespace NetCheck.Services;

public interface IAIEngine
{
    public Task<string> RunAgent();
}
