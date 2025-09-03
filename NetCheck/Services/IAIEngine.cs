using Microsoft.Extensions.AI;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NetCheck.Services
{
    public interface IAIEngine
    {
        Task<IList<ChatMessage>> Test();
    }
}