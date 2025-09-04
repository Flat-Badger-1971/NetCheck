using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using NetCheck.Services;

namespace NetCheck.HostedServices;

public class ModelValidationHostedService(IServiceProvider serviceProvider, ILogger<ModelValidationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting model validation...");

        try
        {
            using (IServiceScope scope = serviceProvider.CreateScope())
            {
                IOllamaModelService ollamaService = scope.ServiceProvider.GetRequiredService<IOllamaModelService>();
                bool isModelReady = await ollamaService.EnsureModelIsLoadedAsync();

                if (isModelReady)
                {
                    logger.LogInformation("Model validation completed successfully. AI features are ready.");
                }
                else
                {
                    logger.LogWarning("Model validation failed. AI features may not work properly. Please check your Ollama configuration and ensure the model is available.");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during model validation. AI features may not work properly.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Model validation service stopped.");

        return Task.CompletedTask;
    }
}
