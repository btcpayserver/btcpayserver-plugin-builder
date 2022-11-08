using Microsoft.AspNetCore.Identity;
using PluginBuilder.HostedServices;
using PluginBuilder.Services;

namespace PluginBuilder;

public class Program
{
    public static Task Main(string[] args)
    {
        var host = new Program();
        return new Program().Start(args);
    }

    public Task Start(string[]? args = null)
    {
        WebApplication app = CreateWebApplication(args);
        return app.RunAsync();
    }

    public WebApplication CreateWebApplication(string[]? args = null)
    {
        WebApplicationBuilder builder = CreateWebApplicationBuilder(args);
        var app = builder.Build();
        Configure(app);
        return app;
    }

    public WebApplicationBuilder CreateWebApplicationBuilder(string[]? args = null)
    {
        var builder = WebApplication.CreateBuilder(args ?? Array.Empty<string>());
        builder.Configuration.AddEnvironmentVariables("PB_");
        AddServices(builder.Configuration, builder.Services);
        return builder;
    }

    public void Configure(WebApplication app)
    {
        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        //        app.MapControllerRoute(
        //name: "default",
        //pattern: "{controller=Home}/{action=Index}/{id?}");
        app.MapControllers();
    }

    public void AddServices(IConfiguration configuration, IServiceCollection services)
    {
        services.AddControllersWithViews();
        services.AddHostedService<DatabaseStartupHostedService>();
        services.AddHostedService<DockerStartupHostedService>();
        services.AddHostedService<AzureStartupHostedService>();
        services.AddSingleton<DBConnectionFactory>();
        services.AddSingleton<BuildService>();
        services.AddSingleton<ProcessRunner>();
        services.AddSingleton<AzureStorageClient>();
    }
}
