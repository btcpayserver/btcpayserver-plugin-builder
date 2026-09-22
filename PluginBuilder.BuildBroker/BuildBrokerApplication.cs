using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PluginBuilder.BuildBroker.Configuration;
using PluginBuilder.BuildBroker.HostedServices;
using PluginBuilder.BuildBroker.Services;
using PluginBuilder.Builds.BuildBroker;
using PluginBuilder.Builds.Services;

namespace PluginBuilder.BuildBroker;

public static class BuildBrokerApplication
{
    public const int MaximumRequestBytes = BuildBrokerProtocol.MaximumRequestBytes;
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 4
    };

    public static WebApplication Create(string[]? args = null, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args ?? []);
        builder.Configuration.AddEnvironmentVariables("PBB_");
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMinutes(3));
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Limits.MaxRequestBodySize = MaximumRequestBytes;
            server.Limits.MaxConcurrentConnections = 64;
            server.Limits.MaxRequestHeaderCount = 32;
            server.Limits.MaxRequestHeadersTotalSize = 8 * 1024;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddSingleton(sp => new BuildBrokerSettings(
            sp.GetRequiredService<IConfiguration>()["TOKEN_FILE"] ??
                throw new InvalidOperationException("TOKEN_FILE is required."), TimeSpan.FromMinutes(45)));
        builder.Services.AddSingleton<BrokerAuthentication>();
        builder.Services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            return BuildExecutorOptions.FromConfiguration(config, builder.Environment.IsDevelopment());
        });
        builder.Services.AddSingleton<ProcessRunner>();
        builder.Services.AddSingleton<BuildExecutorState>();
        builder.Services.AddSingleton<BuildScratchCleaner>();
        builder.Services.AddSingleton<DockerBuildSandbox>();
        builder.Services.AddSingleton<IBuildSandbox>(sp => sp.GetRequiredService<DockerBuildSandbox>());
        builder.Services.AddSingleton<BrokerCoordinator>();
        builder.Services.AddHostedService<DockerStartupHostedService>();
        builder.Services.AddHostedService<BrokerDockerMonitor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<BrokerCoordinator>());
        configure?.Invoke(builder);
        var app = builder.Build();
        var authentication = app.Services.GetRequiredService<BrokerAuthentication>();

        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (!authentication.Authorize(context.Request))
            {
                context.Response.StatusCode = 401;
                return;
            }
            var instance = context.RequestServices.GetRequiredService<BrokerCoordinator>().InstanceId;
            context.Response.Headers[BuildBrokerProtocol.InstanceHeader] = instance;
            var expected = context.Request.Headers[BuildBrokerProtocol.InstanceHeader];
            if ((context.Request.Path.StartsWithSegments("/v1/builds") && context.Request.Path != "/v1/builds" &&
                    expected.Count != 1) ||
                (expected.Count > 0 && (expected.Count != 1 || expected[0] != instance)))
            {
                context.Response.StatusCode = 409;
                return;
            }
            try { await next(context); }
            catch (BrokerRequestException error) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = error.StatusCode;
                await context.Response.WriteAsJsonAsync(new { error = error.Message });
            }
            catch (BadHttpRequestException error) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = error.StatusCode;
            }
            catch (JsonException) when (!context.Response.HasStarted) { context.Response.StatusCode = 400; }
            catch (OperationCanceledException) when (!context.Response.HasStarted) { context.Response.StatusCode = 408; }
            catch (Exception error)
            {
                app.Logger.LogError(error, "Broker operation failed");
                if (context.Response.HasStarted) context.Abort();
                else
                {
                    context.Response.StatusCode = 503;
                    await context.Response.WriteAsJsonAsync(new { error = "The build broker operation failed." });
                }
            }
        });
        app.MapGet("/v1/status", (BrokerCoordinator coordinator) => Results.Json(coordinator.Status()));
        app.MapPost("/v1/builds", async (HttpContext context, BrokerCoordinator coordinator) =>
        {
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
            // Kestrel's MaxRequestBodySize answers oversized bodies with 413. The serializer
            // rejects duplicate, unknown, missing and null-for-non-nullable members (400).
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var request = await JsonSerializer.DeserializeAsync<BrokerBuildRequest>(context.Request.Body, RequestJson, deadline.Token);
            return request is null ? Results.BadRequest() : Results.Json(coordinator.Submit(request), statusCode: 202);
        });
        app.MapGet("/v1/builds/{id}", (string id, HttpContext context, BrokerCoordinator coordinator) =>
        {
            var value = context.Request.Query["cursor"];
            var cursor = 0;
            if (value.Count > 1 || (value.Count == 1 &&
                !int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out cursor)))
                return Results.BadRequest();
            return Results.Json(coordinator.ReadStatus(id, cursor));
        });
        app.MapGet("/v1/builds/{id}/artifact", async (string id, HttpContext context, BrokerCoordinator coordinator) =>
            await coordinator.WriteArtifactAsync(id, context));
        return app;
    }

    public static async Task<int> Healthcheck()
    {
        try
        {
            var authentication = new BrokerAuthentication(new(
                Environment.GetEnvironmentVariable("PBB_TOKEN_FILE") ?? "", TimeSpan.FromMinutes(45)));
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false })
                { Timeout = TimeSpan.FromSeconds(5) };
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:8080/v1/status");
            request.Headers.TryAddWithoutValidation("Authorization", authentication.HeaderForHealthcheck());
            using var response = await client.SendAsync(request);
            var status = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<BrokerStatus>() : null;
            return status?.IsReady == true ? 0 : 1;
        }
        catch { return 1; }
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--healthcheck"]) return await BuildBrokerApplication.Healthcheck();
        await using var app = BuildBrokerApplication.Create(args);
        await app.RunAsync();
        return 0;
    }
}
