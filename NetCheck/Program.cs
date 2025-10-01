using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetCheck.Extensions;
using NetCheck.HostedServices;
using NetCheck.Services;
using NetCheck.Logging;
using Microsoft.Extensions.Logging.Console;
using System.Diagnostics.CodeAnalysis;

namespace NetCheck;

public static class Program
{
    [ExcludeFromCodeCoverage]
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ConsoleFormatter, MinimalConsoleFormatter>();

        // custom console formatter for prettiness
        builder.Logging.AddConsole(options =>
        {
            options.FormatterName = MinimalConsoleFormatter.FormatterName;
        });

        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        // extension method to add AI services
        builder.AddAIServices();

        builder.Services.AddSingleton<IAIEngine, AIEngine>();
        builder.Services.AddHostedService<ModelValidationHostedService>();

        WebApplication app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();
        app.UseAuthorization();
        app.MapControllers();
        app.Run();
    }
}
