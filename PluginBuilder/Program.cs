using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Npgsql;
using PluginBuilder.Authentication;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.HostedServices;
using PluginBuilder.Hubs;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Serilog;
using Serilog.Extensions.Logging;
using PluginBuilder.Configuration;

namespace PluginBuilder;

public class Program
{
    public static Task Main(string[] args)
    {
        Program host = new();
        return new Program().Start(args);
    }

    public async Task Start(string[]? args = null)
    {
        var app = CreateWebApplication(args);
        try
        {
            await app.RunAsync();
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    public WebApplication CreateWebApplication(string[]? args = null)
    {
        var builder = CreateWebApplicationBuilder(args);
        var app = builder.Build();
        Configure(app);
        return app;
    }

    public WebApplicationBuilder CreateWebApplicationBuilder(string[]? args = null)
    {
        var builder = WebApplication.CreateBuilder(args ?? Array.Empty<string>());
        ConfigureBuilder(builder);
        return builder;
    }

    public WebApplicationBuilder CreateWebApplicationBuilder(WebApplicationOptions options)
    {
        var builder = WebApplication.CreateBuilder(options);
        ConfigureBuilder(builder);
        return builder;
    }

    private void ConfigureBuilder(WebApplicationBuilder builder)
    {
        builder.Configuration.AddEnvironmentVariables("PB_");

#if DEBUG
        builder.Logging.AddFilter(typeof(ProcessRunner).FullName, LogLevel.Trace);
#endif
        var verbose = builder.Configuration.GetValue<bool>("verbose");
        if (!verbose)
            builder.Logging.AddFilter("Events", LogLevel.Warning);

        // Uncomment this to see EF queries
        // builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Trace);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Migrations", LogLevel.Information);
        builder.Logging.AddFilter("Microsoft", LogLevel.Error);
        builder.Logging.AddFilter("Microsoft.Hosting", LogLevel.Information);
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Critical);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Antiforgery.Internal", LogLevel.Critical);

        AddServices(builder.Configuration, builder.Services, builder.Environment);
    }

    public void Configure(WebApplication app)
    {
        ForwardedHeadersOptions forwardingOptions = new() { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
        forwardingOptions.KnownNetworks.Clear();
        forwardingOptions.KnownProxies.Clear();
        forwardingOptions.ForwardedHeaders = ForwardedHeaders.All;
        app.UseForwardedHeaders(forwardingOptions);

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Home/Error");
            app.UseHsts();
        }

        // Capture base URL once on first request for FirstBuildEvents
        app.Use(async (ctx, next) =>
        {
            var fbe = ctx.RequestServices.GetRequiredService<FirstBuildEvent>();
            if (ctx.Request.Host.HasValue)
            {
                var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
                fbe.InitBaseUrl(baseUrl);
            }
            await next();
        });

        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseOutputCache();
        app.MapHub<PluginHub>("hub");
        app.MapHub<PluginHub>("/plugins/{pluginSlug}/hub");
        app.MapHub<PluginHub>("/plugins/{pluginSlug}/builds/{buildId}/hub");
        // no default routes
        app.MapControllers();
    }

    public void AddServices(IConfiguration configuration, IServiceCollection services, IHostEnvironment env)
    {
        services.AddControllersWithViews()
            .AddRazorRuntimeCompilation()
            .AddRazorOptions(options =>
            {
                options.ViewLocationFormats.Add("/{0}.cshtml");
            })
            .AddNewtonsoftJson(o => o.SerializerSettings.Formatting = Formatting.Indented)
            .AddApplicationPart(typeof(Program).Assembly);

        var pbOptions = PluginBuilderOptions.ConfigureDataDirAndDebugLog(configuration, env);
        services.AddSingleton(pbOptions);

        services.AddDataProtection()
            .SetApplicationName("Plugin Builder")
            .PersistKeysToFileSystem(new DirectoryInfo(pbOptions.DataDir));

        const long maxDebugLogFileSize = 2_000_000;

        services.AddLogging(logBuilder =>
        {
            var debugLogFile = pbOptions.DebugLogFile;
            if (string.IsNullOrEmpty(debugLogFile)) return;

            Log.Logger = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .MinimumLevel.Is(pbOptions.DebugLogLevel ?? Serilog.Events.LogEventLevel.Information)
                .WriteTo.File(debugLogFile,
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: maxDebugLogFileSize,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: pbOptions.LogRetainCount)
                .CreateLogger();

            logBuilder.AddProvider(new SerilogLoggerProvider(Log.Logger));
        });

        services.AddHostedService<DatabaseStartupHostedService>();
        services.AddHostedService<DockerStartupHostedService>();
        services.AddHostedService<AzureStartupHostedService>();
        services.AddHostedService<PluginHubHostedService>();

        services.AddSingleton<DBConnectionFactory>();
        services.AddSingleton<BuildService>();
        services.AddSingleton<ProcessRunner>();
        services.AddSingleton<GPGKeyService>();
        services.AddSingleton<AzureStorageClient>();
        services.AddSingleton<ServerEnvironment>();
        services.AddSingleton<EventAggregator>();
        services.AddHttpClient();
        services.AddSingleton<ExternalAccountVerificationService>();
        services.AddSingleton<EmailService>();
        services.AddSingleton<FirstBuildEvent>();
        services.AddSingleton<NostrService>();

        // shared controller logic
        services.AddSingleton<AdminSettingsCache>();
        services.AddTransient<UserVerifiedLogic>();
        services.AddScoped<ReferrerNavigationService>();
        services.AddHttpContextAccessor();
        services.AddSingleton<IActionContextAccessor, ActionContextAccessor>();
        services.AddScoped<IUrlHelper>(sp => {
            var actionContext = sp.GetRequiredService<IActionContextAccessor>().ActionContext;
            return new UrlHelper(actionContext);
        });

        services.AddOutputCache(options =>
        {
            options.AddPolicy("PluginsList", p => p
                .Expire(TimeSpan.FromSeconds(60))
                .SetVaryByQuery("btcpayVersion", "includePreRelease", "includeAllVersions", "searchPluginName")
                .Tag(CacheTags.Plugins));
        });

        services.AddDbContext<IdentityDbContext<IdentityUser>>(b =>
        {
            b.UseNpgsql(configuration.GetRequired("POSTGRES"));
        });
        NpgsqlConnection.GlobalTypeMapper.MapEnum<PluginVisibilityEnum>("plugin_visibility_enum");

        services.AddIdentity<IdentityUser, IdentityRole>(options =>
            {
                options.Password.RequireDigit = false;
                options.Password.RequiredLength = 6;
                options.Password.RequireLowercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.AllowedForNewUsers = true;
                options.Password.RequireUppercase = false;
            })
            .AddDefaultTokenProviders()
            .AddEntityFrameworkStores<IdentityDbContext<IdentityUser>>();

        services.PostConfigure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, opt =>
        {
            opt.LoginPath = "/login";
            opt.AccessDeniedPath = null;
            opt.LogoutPath = "/logout";
        });
        services.AddAuthentication()
            .AddScheme<PluginBuilderAuthenticationOptions, BasicAuthenticationHandler>(PluginBuilderAuthenticationSchemes.BasicAuth, o => { });
        services.AddAuthorization(o =>
        {
            o.AddPolicy(Policies.OwnPlugin, o => o.AddRequirements(new OwnPluginRequirement()));
        });
        services.AddScoped<IAuthorizationHandler, PluginBuilderAuthorizationHandler>();
        services.AddSignalR();
    }
}
