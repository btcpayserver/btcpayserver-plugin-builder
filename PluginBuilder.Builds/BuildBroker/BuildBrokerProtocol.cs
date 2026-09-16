namespace PluginBuilder.Builds.BuildBroker;

public static class BuildBrokerProtocol
{
    public const string InstanceHeader = "X-Build-Broker-Instance";
    public const int MaximumRequestBytes = 16 * 1024;
    public const int MaximumStatusBytes = 16 * 1024 * 1024;
    public const long MaximumArtifactBytes = 256L * 1024 * 1024;
}

// This is a build protocol, deliberately not a Docker/OCI configuration API.
public sealed record BrokerBuildRequest(string PluginSlug, long BuildId, string GitRepository,
    string? GitRef, string? PluginDir, string? BuildConfig);
public sealed record BrokerBuildAccepted(string LeaseId);
public sealed record BrokerStatus(bool IsReady, string? WorkerImageId, string? ProxyImageId,
    string? UnavailableReason, string InstanceId);
public sealed record BrokerBuildStatus(string State, int NextCursor, string[] Logs,
    BrokerBuildResult? Result, string? Error);
public sealed record BrokerBuildResult(string BuildEnvironmentJson, string ManifestJson,
    string AssemblyName, long ArtifactLength, string ArtifactSha256);
