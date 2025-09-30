using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetCheck.Extensions;
using NetCheck.HostedServices;
using NetCheck.Services;
using NetCheck.Logging;
using Microsoft.Extensions.Logging.Console;

namespace NetCheck;

public static class Program
{
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ConsoleFormatter, MinimalConsoleFormatter>();
        builder.Logging.AddConsole(options =>
        {
            options.FormatterName = MinimalConsoleFormatter.FormatterName;
        });

        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
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
