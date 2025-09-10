using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetCheck.Extensions;
using NetCheck.HostedServices;
using NetCheck.Services;
using NetCheck.Logging;
using Microsoft.Extensions.Logging.Console; // added

namespace NetCheck;

public static class Program
{
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        // Configure logging to use minimal console formatter
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ConsoleFormatter, MinimalConsoleFormatter>();
        builder.Logging.AddConsole(options =>
        {
            options.FormatterName = MinimalConsoleFormatter.FormatterName;
        });

        // Add services to the container.
        builder.Services.AddControllers();
        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        
        // Add AI services (this will register OllamaService internally)
        builder.AddAIServices();

        // Register services
        builder.Services.AddSingleton<IAIEngine, AIEngine>();

        // Register model validation hosted service
        builder.Services.AddHostedService<ModelValidationHostedService>();

        WebApplication app = builder.Build();

        // Configure the HTTP request pipeline.
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
