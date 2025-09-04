using System.Threading;
using System.Threading.Tasks;

namespace NetCheck.Services;

public interface IAIEngine
{
    public Task<string> ScanRepositoryAsync(string repository, CancellationToken cancellationToken = default);
}
