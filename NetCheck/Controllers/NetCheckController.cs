using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;
using NetCheck.Services;

namespace NetCheck.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class TradingController(ILogger<TradingController> logger, IAIEngine engine) : ControllerBase
    {
        [HttpGet(Name = "Test")]
        public async Task<IActionResult> Get()
        {
            IList<ChatMessage> result = await engine.Test();

            return Ok(result);
        }
    }
}
