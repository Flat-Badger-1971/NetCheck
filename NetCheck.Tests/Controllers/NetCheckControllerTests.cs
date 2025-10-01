using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NetCheck.Controllers;
using NetCheck.Services;
using Xunit;

namespace NetCheck.Tests.Controllers;

public class NetCheckControllerTests
{
    [Fact]
    public async Task Get_ReturnsOk_WithEngineResult()
    {
        // Arrange
        IAIEngine engine = Substitute.For<IAIEngine>();
        engine.RunAgent().Returns("result");
        NetCheckController controller = new NetCheckController(engine);

        // Act
        IActionResult actionResult = await controller.Get();

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(actionResult);
        Assert.Equal("result", okResult.Value);
        await engine.Received(1).RunAgent();
    }
}
