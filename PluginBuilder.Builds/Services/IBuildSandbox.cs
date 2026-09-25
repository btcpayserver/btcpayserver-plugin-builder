using Newtonsoft.Json.Linq;
using PluginBuilder.Util;

namespace PluginBuilder.Builds.Services;

public interface IBuildSandbox
{
    Task<IPreparedBuild> PrepareAsync(
        FullBuildId buildId, BuildInfo buildInfo, CancellationToken cancellationToken = default);
}

public interface IPreparedBuild : IAsyncDisposable
{
    Task<StagedBuildOutput> RunAndStageAsync(IOutputCapture buildOutput);
}

public sealed record StagedBuildOutput(
    JObject BuildEnvironment, string ManifestJson, string AssemblyName, string StagingDirectory);
