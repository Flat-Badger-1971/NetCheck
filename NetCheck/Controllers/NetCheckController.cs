using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NetCheck.Services;

namespace NetCheck.Controllers;

[ApiController]
[Route("[controller]")]
public class NetCheckController(IAIEngine engine) : ControllerBase
{
    // GET /NetCheck/{repo}
    // Example: /NetCheck/owner/repository-name
    [HttpGet("{*repo}")]
    public async Task<IActionResult> Get(string repo, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repo))
        {
            return BadRequest("Repository path or identifier is required.");
        }

        string json = await engine.ScanRepositoryAsync(repo, cancellationToken);
        return Content(json, "application/json");
    }

    [HttpGet("interactive")]
    public async Task Get()
    {
        engine.Interactive();
    }
}
