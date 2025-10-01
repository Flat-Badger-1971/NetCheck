using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NetCheck.Services;

namespace NetCheck.Controllers;

[ApiController]
[Route("[controller]")]
public class NetCheckController(IAIEngine engine) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        string response = await engine.RunAgent();
        return Ok(response);
    }
}
