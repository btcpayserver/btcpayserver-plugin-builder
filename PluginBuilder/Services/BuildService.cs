using System.Threading.Channels;
using System.Xml.Linq;
using Dapper;
using Newtonsoft.Json.Linq;
using PluginBuilder.DataModels;
using PluginBuilder.Events;
using PluginBuilder.JsonConverters;
using PluginBuilder.Util;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Services;

public class BuildServiceException(string message) : Exception(message);

public class BuildService
{
    private static readonly SemaphoreSlim _semaphore = new(5);
    private readonly IHttpClientFactory _httpClientFactory;

    public BuildService(
        ILogger<BuildService> logger,
        ProcessRunner processRunner,
        DBConnectionFactory connectionFactory,
        EventAggregator eventAggregator,
        AzureStorageClient azureStorageClient,
        IHttpClientFactory httpClientFactory)
    {
        Logger = logger;
        ProcessRunner = processRunner;
        ConnectionFactory = connectionFactory;
        EventAggregator = eventAggregator;
        AzureStorageClient = azureStorageClient;
        _httpClientFactory = httpClientFactory;
    }

    public ILogger<BuildService> Logger { get; }
    public ProcessRunner ProcessRunner { get; }
    public DBConnectionFactory ConnectionFactory { get; }
    public EventAggregator EventAggregator { get; }
    public AzureStorageClient AzureStorageClient { get; }

    private HttpClient GitHubClient()
    {
        return _httpClientFactory.CreateClient(HttpClientNames.GitHub);
    }

    public async Task Build(FullBuildId fullBuildId)
    {
        BuildInfo buildParameters;
        await _semaphore.WaitAsync();
        try
        {
            using BuildOutputCapture buildLogCapture = new(fullBuildId, ConnectionFactory);
            List<string> args = new();
            buildParameters = await GetBuildInfo(fullBuildId);
            // Create the volumes where the artifacts will be stored
            args.AddRange(new[] { "volume", "create" });
            args.AddRange(new[] { "--label", $"BTCPAY_PLUGIN_BUILD={fullBuildId}" });
            int code;
            string volume;
            try
            {
                OutputCapture output = new();
                code = await ProcessRunner.RunAsync(
                    new ProcessSpec
                    {
                        Executable = "docker",
                        Arguments = args.ToArray(),
                        OutputCapture = output
                    },
                    default);
                if (code != 0)
                    throw new BuildServiceException("docker volume create failed");
                volume = output.ToString().Trim();
                args.Clear();

                // Then let's build by running our image plugin-builder (built in DockerStartupHostedService)
                JObject info = new();

                args.Add("run");
                args.AddRange(new[] { "--env", $"GIT_REPO={buildParameters.GitRepository}" });
                info["gitRepository"] = buildParameters.GitRepository;
                info["dockerVolume"] = volume;
                if (buildParameters.GitRef != null)
                {
                    args.AddRange(new[] { "--env", $"GIT_REF={buildParameters.GitRef}" });
                    info["gitRef"] = buildParameters.GitRef;
                }

                if (buildParameters.PluginDir != null)
                {
                    args.AddRange(new[] { "--env", $"PLUGIN_DIR={buildParameters.PluginDir}" });
                    info["pluginDir"] = buildParameters.PluginDir;
                }

                if (buildParameters.BuildConfig != null)
                {
                    args.AddRange(new[] { "--env", $"BUILD_CONFIG={buildParameters.BuildConfig}" });
                    info["buildConfig"] = buildParameters.BuildConfig;
                }

                args.AddRange(new[] { "-v", $"{volume}:/out" });
                args.AddRange(new[] { "--rm" });
                args.Add("plugin-builder");
                await UpdateBuild(fullBuildId, BuildStates.Running, info);
            }
            catch (Exception err)
            {
                await UpdateBuild(fullBuildId, BuildStates.Failed, new JObject { ["error"] = err.Message });
                throw;
            }

            JObject buildEnv;
            try
            {
                code = await ProcessRunner.RunAsync(new ProcessSpec
                {
                    Executable = "docker",
                    Arguments = args.ToArray(),
                    OutputCapture = buildLogCapture,
                    ErrorCapture = buildLogCapture,
                    OnOutput = (_, eventArgs) =>
                    {
                        if (!string.IsNullOrEmpty(eventArgs.Data))
                            EventAggregator.Publish(new BuildLogUpdated(fullBuildId, eventArgs.Data));
                    },
                    OnError = (_, eventArgs) =>
                    {
                        if (!string.IsNullOrEmpty(eventArgs.Data))
                            EventAggregator.Publish(new BuildLogUpdated(fullBuildId, eventArgs.Data));
                    }
                }, default);
                if (code != 0)
                    throw new BuildServiceException("docker build failed");

                var buildEnvStr = await ReadFileInVolume(volume, "build-env.json");
                buildEnv = JObject.Parse(buildEnvStr);
            }
            catch (Exception err)
            {
                await UpdateBuild(fullBuildId, BuildStates.Failed, new JObject { ["error"] = err.Message });
                throw;
            }

            var assemblyName = buildEnv["assemblyName"]!.Value<string>();
            var manifestStr = await ReadFileInVolume(volume, $"{assemblyName}.btcpay.json");

            PluginManifest manifest;
            try
            {
                manifest = PluginManifest.Parse(manifestStr);
                await UpdateBuild(fullBuildId, BuildStates.WaitingUpload, buildEnv, manifest);
            }
            catch (Exception err)
            {
                await UpdateBuild(fullBuildId, BuildStates.Failed,
                    new JObject { ["error"] = "Invalid plugin manifest: " + err.Message });
                throw;
            }

            await UpdateBuild(fullBuildId, BuildStates.Uploading, null);
            string url;
            try
            {
                url = await AzureStorageClient.Upload(volume, $"{assemblyName}.btcpay",
                    $"{fullBuildId}/{assemblyName}.btcpay");
            }
            catch (Exception err)
            {
                await UpdateBuild(fullBuildId, BuildStates.Failed, new JObject { ["error"] = err.Message });
                throw;
            }

            await UpdateBuild(fullBuildId, BuildStates.Uploaded, new JObject { ["url"] = url });
            await SetVersionBuild(fullBuildId, manifest, buildLogCapture);
        }
        finally
        {
            _semaphore.Release();
        }
        await SavePluginContributorSnapshot(fullBuildId.PluginSlug, buildParameters);
    }

    private async Task SavePluginContributorSnapshot(PluginSlug pluginSlug, BuildInfo buildInfo)
    {
        try
        {
            var githubClient = _httpClientFactory.CreateClient(HttpClientNames.GitHub);
            var contributors = await GithubService.GetContributorsAsync(githubClient, buildInfo.GitRepository, buildInfo.PluginDir);
            await GithubService.SaveSnapshotAsync(pluginSlug, contributors);
        }
        catch (Exception) { }
    }

    private async Task<BuildInfo> GetBuildInfo(FullBuildId fullBuildId)
    {
        await using var connection = await ConnectionFactory.Open();
        var buildInfo = await connection.QueryFirstOrDefaultAsync<string>("SELECT build_info FROM builds WHERE plugin_slug=@pluginSlug AND id=@buildId",
            new { pluginSlug = fullBuildId.PluginSlug.ToString(), buildId = fullBuildId.BuildId });
        if (buildInfo is null)
            throw new BuildServiceException("This build doesn't exists");
        return BuildInfo.Parse(buildInfo);
    }

    private async Task SetVersionBuild(FullBuildId fullBuildId, PluginManifest manifest, IOutputCapture buildLogs)
    {
        await using var connection = await ConnectionFactory.Open();
        if (await connection.EnsureIdentifierOwnership(fullBuildId.PluginSlug, manifest.Identifier))
            await connection.SetVersionBuild(fullBuildId, manifest.Version, manifest.BTCPayMinVersion, true);
        else
            buildLogs.AddLine($"The plugin identifier {manifest.Identifier} doesn't belong to this project slug");
    }

    private async Task<string> ReadFileInVolume(string volume, string file)
    {
        OutputCapture output = new();
        // Let's read the build-env.json
        var code = await ProcessRunner.RunAsync(
            new ProcessSpec
            {
                Executable = "docker",
                Arguments = new[] { "run", "--rm", "-v", $"{volume}:/out", "plugin-builder", "cat", $"/out/{file}" },
                OutputCapture = output
            }, default);
        if (code != 0)
            throw new BuildServiceException("docker run to read a file in volume");
        return output.ToString();
    }

    public async Task UpdateBuild(FullBuildId fullBuildId, BuildStates newState, JObject? buildInfo, PluginManifest? manifestInfo = null)
    {
        await using var connection = await ConnectionFactory.Open();
        await connection.UpdateBuild(fullBuildId, newState, buildInfo, manifestInfo);
        EventAggregator.Publish(new BuildChanged(fullBuildId, newState) { BuildInfo = buildInfo?.ToString(), ManifestInfo = manifestInfo?.ToString() });
    }

    public async Task<string> FetchIdentifierFromGithubCsprojAsync(string repoUrl, string gitRef, string? pluginDir = null)
    {
        var githubClient = GitHubClient();

        var (owner, repo) = ExtractOwnerRepo(repoUrl);
        var dir = string.IsNullOrWhiteSpace(pluginDir) ? "" : pluginDir.Trim('/');

        var encodedDir = string.Join('/', dir.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        var apiUrl = $"repos/{owner}/{repo}/contents" + (string.IsNullOrEmpty(encodedDir) ? "" : "/" + encodedDir);

        if (!string.IsNullOrWhiteSpace(gitRef))
            apiUrl += $"?ref={Uri.EscapeDataString(gitRef.Trim())}";

        using var resp = await githubClient.GetAsync(apiUrl);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new BuildServiceException(
                $"GitHub error ({(int)resp.StatusCode}) listing '{(string.IsNullOrEmpty(dir) ? "/" : dir)}': {apiUrl}\nBody: {body}");

        if (body.TrimStart().StartsWith('{'))
            throw new BuildServiceException(
                $"Expected directory listing but GitHub returned an object. Check pluginDir='{pluginDir}' (must be a directory). Path: {apiUrl}");

        var items = SafeJson.Deserialize<List<GithubContentItem>>(body);
        var csprojs = items
            .Where(i => string.Equals(i.type, "file", StringComparison.OrdinalIgnoreCase)
                        && i.name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToList();

        if (csprojs == null || csprojs.Count == 0)
            throw new BuildServiceException(
                $"No .csproj found in '{(string.IsNullOrEmpty(dir) ? "/" : dir)}' at {(string.IsNullOrWhiteSpace(gitRef) ? "default branch" : gitRef)}.");
        if (csprojs.Count > 1)
            throw new BuildServiceException("Multiple .csproj found. Keep exactly one.");

        var downloadUrl = csprojs[0].download_url;
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new BuildServiceException($"GitHub item '{csprojs[0].name}' has no download_url.");

        using var csprojResp = await githubClient.GetAsync(downloadUrl);
        var csprojBody = await csprojResp.Content.ReadAsStringAsync();

        if (!csprojResp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(csprojBody))
            throw new BuildServiceException(
                $"GitHub error downloading '{csprojs[0].name}' from {downloadUrl} (HTTP {(int)csprojResp.StatusCode}).\nBody: {csprojBody}");

        var doc = XDocument.Parse(csprojBody);
        var assemblyName = doc.Descendants("AssemblyName").FirstOrDefault()?.Value ?? Path.GetFileNameWithoutExtension(csprojs[0].name);

        return assemblyName;
    }

    private (string owner, string repo) ExtractOwnerRepo(string repoUrl)
    {
        repoUrl = repoUrl.Trim().Replace(".git", "");

        if (!repoUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            repoUrl = "https://" + repoUrl.TrimStart('/');

        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri))
            throw new BuildServiceException("Invalid repository URL");

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2)
            throw new BuildServiceException("Invalid repository URL");
        return (parts[0], parts[1]);
    }


    public class BuildOutputCapture : IOutputCapture, IDisposable
    {
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>();

        public BuildOutputCapture(FullBuildId fullBuildId, DBConnectionFactory connectionFactory)
        {
            FullBuildId = fullBuildId;
            ConnectionFactory = connectionFactory;
            _ = SaveLoop();
        }

        private FullBuildId FullBuildId { get; }
        private DBConnectionFactory ConnectionFactory { get; }

        public void Dispose()
        {
            lines.Writer.TryComplete();
        }

        public void AddLine(string line)
        {
            lines.Writer.TryWrite(line);
        }

        private async Task SaveLoop()
        {
            while (await lines.Reader.WaitToReadAsync())
            {
                List<string> rows = new();
                while (lines.Reader.TryRead(out var l))
                    rows.Add(l);
                await using var conn = await ConnectionFactory.Open();
                await conn.ExecuteAsync("INSERT INTO builds_logs VALUES (@pluginSlug, @buildId, @log)",
                    rows.Select(row =>
                        new
                        {
                            pluginSlug = FullBuildId.PluginSlug.ToString(),
                            buildId = FullBuildId.BuildId,
                            log = row
                        }).ToArray());
            }
        }
    }

    private record GithubContentItem(string name, string type, string? download_url);
}
