using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Npgsql;
using PluginBuilder.Authentication;
using PluginBuilder.Configuration;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using PluginBuilder.Filters;
using PluginBuilder.HostedServices;
using PluginBuilder.Hubs;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

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

        builder.Services.AddHealthChecks().AddCheck<HealthService>("Dependencies");

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
        // ForwardedHeaders.All + cleared KnownNetworks/KnownProxies is required because proxy IPs are dynamic
        // in Docker (populating KnownProxies would create a circular dependency in docker-compose).
        // This means X-Forwarded-Host is trusted from any source. Host header poisoning is mitigated by:
        // 1. nginx explicitly setting X-Forwarded-Host (see btcpayserver-plugin-builder-infra/nginx.tmpl)
        // 2. PB_ALLOWEDHOSTS env var restricting accepted hostnames via HostFilteringMiddleware
        // https://github.com/btcpayserver/btcpayserver-plugin-builder-infra/pull/2
        ForwardedHeadersOptions forwardingOptions = new() { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
        forwardingOptions.KnownNetworks.Clear();
        forwardingOptions.KnownProxies.Clear();
        forwardingOptions.ForwardedHeaders = ForwardedHeaders.All;
        app.UseForwardedHeaders(forwardingOptions);

        if (!app.Environment.IsDevelopment())
            app.UseHsts();

        app.UseStatusCodePagesWithReExecute("/errors/{0}");
        app.UseExceptionHandler("/errors/500");

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

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                // This allows resources such as fonts to load in an iframe
                // we use iframes in BTCPay Server plugin page.
                ctx.Context.Response.Headers.AccessControlAllowOrigin = "*";
                ctx.Context.Response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";
            }
        });
        app.UseRouting();
        app.UseRateLimiter();
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
        services.AddControllersWithViews(options =>
            {
                options.Filters.Add(new UIControllerAntiforgeryTokenAttribute());
            })
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
            if (string.IsNullOrEmpty(debugLogFile))
                return;

            Log.Logger = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .MinimumLevel.Is(pbOptions.DebugLogLevel ?? LogEventLevel.Information)
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
        services.AddHostedService<PluginCleanupHostedService>();
        services.AddHostedService<UserCleanupHostedService>();

        services.AddSingleton<DBConnectionFactory>();
        services.AddScoped<PluginCleanupRunner>();
        services.AddScoped<UserCleanupRunner>();
        services.AddSingleton<BuildService>();
        services.AddSingleton<ProcessRunner>();
        services.AddSingleton<GPGKeyService>();
        services.AddSingleton<AzureStorageClient>();
        services.AddSingleton<ServerEnvironment>();
        services.AddSingleton<EventAggregator>();
        services.AddSingleton<HealthService>();
        services.AddHttpClient(HttpClientNames.GitHub, client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.Add("User-Agent", "PluginBuilder");

            var token = configuration["GITHUB_TOKEN"];
            if (!string.IsNullOrWhiteSpace(token))
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
        });
        services.AddHttpClient(HttpClientNames.BtcMapsDirectory, client =>
        {
            // Per-call timeout caps a single GitHub round-trip at 15s. The directory
            // submission makes ~5-7 GitHub calls sequentially; with the default 100s
            // timeout a hung remote could pin the request for ~10min and tie up a
            // rate-limit slot. 15s per call keeps the worst case bounded.
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.Add("User-Agent", "PluginBuilder-BtcMaps/1.0");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddHttpClient(HttpClientNames.BtcMap, client =>
        {
            // BTC Map import RPC is a single JSON-RPC 2.0 dispatch endpoint.
            // Per-call timeout caps a single round-trip at 15s, matching the
            // BtcMapsDirectory budget so a hung remote can't pin the request
            // longer than the per-IP rate-limit window.
            client.DefaultRequestHeaders.Add("User-Agent", "PluginBuilder-BtcMap/1.0");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddHttpClient(HttpClientNames.GitLab, client =>
        {
            client.BaseAddress = new Uri("https://gitlab.com/api/v4/");
            client.DefaultRequestHeaders.Add("User-Agent", "PluginBuilder");

            var token = configuration["GITLAB_TOKEN"];
            if (!string.IsNullOrWhiteSpace(token))
                client.DefaultRequestHeaders.Add("PRIVATE-TOKEN", token);
        });
        services.AddSingleton<IGitHostingProvider, GitHubHostingProvider>();
        services.AddSingleton<IGitHostingProvider, GitLabHostingProvider>();
        services.AddSingleton<GitHostingProviderFactory>();
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
        services.AddScoped<IUrlHelper>(sp =>
        {
            var actionContext = sp.GetRequiredService<IActionContextAccessor>().ActionContext;
            return new UrlHelper(actionContext);
        });
        services.AddScoped<PluginOwnershipService>();
        services.AddScoped<VersionLifecycleService>();
        services.AddSingleton<BtcMapsService>();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.ContentType = "application/json";
                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    code = "429",
                    message = "Too many requests. Please try again later."
                }, cancellationToken);
            };
            options.AddPolicy(Policies.PublicApiRateLimit, httpContext =>
            {
                var cache = httpContext.RequestServices.GetRequiredService<AdminSettingsCache>();
                var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(clientIp, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = cache.RateLimitPermitLimit,
                    Window = TimeSpan.FromSeconds(cache.RateLimitWindowSeconds),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                });
            });
            options.AddPolicy(Policies.BtcMapsSubmitRateLimit, httpContext =>
            {
                // Per-source-IP fixed window: 3 submissions per 24h. Caps automation
                // abuse of /apis/btcmaps/v1/submit without throttling honest single
                // submissions from a merchant. Tightened from 5/24h with the
                // multi-vendor BTC Map import-RPC lane (PR #226) since that path
                // forwards into a moderator review queue and rate-limit is the
                // primary spam control on the public endpoint.
                var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(clientIp, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 3,
                    Window = TimeSpan.FromHours(24),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                });
            });
        });

        services.AddOutputCache(options =>
        {
            options.AddPolicy("PluginsList", p => p
                .Expire(TimeSpan.FromSeconds(60))
                .SetVaryByQuery("btcpayVersion", "includePreRelease", "includeAllVersions", "searchPluginName")
                .Tag(CacheTags.Plugins));
        });

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(configuration.GetRequired("POSTGRES"));
        dataSourceBuilder.MapEnum<PluginVisibilityEnum>("plugin_visibility_enum");
        var dataSource = dataSourceBuilder.Build();

        services.AddDbContext<IdentityDbContext<IdentityUser>>(b =>
        {
            b.UseNpgsql(dataSource);
        });

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
            opt.AccessDeniedPath = "/errors/403";
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
