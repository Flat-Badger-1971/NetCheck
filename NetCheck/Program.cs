using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetCheck.Extensions;
using NetCheck.Services;
// using NetCheck.Tools;
using NetCheck.HostedServices;

namespace NetCheck;

public class Program
{
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        // Add services to the container.

        builder.Services.AddControllers();
        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        // options
        ConfigurationManager configuration = builder.Configuration;

        // builder.Services.Configure<Trading212Settings>(configuration.GetSection("Trading212"));
        
        // Add AI services (this will register OllamaService internally)
        builder.AddAIServices();

        // Add HttpClient for Trading212 service
        // builder.Services.AddHttpClient<Trading212Service>();

        // Register services
        builder.Services.AddSingleton<IAIEngine, AIEngine>();
        // builder.Services.AddSingleton<Trading212Service>();

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
