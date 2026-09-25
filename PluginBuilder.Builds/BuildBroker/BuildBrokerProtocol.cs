using System.Diagnostics.CodeAnalysis;
using System.Text;
using PluginBuilder.Builds.Services;

namespace PluginBuilder.Builds.BuildBroker;

public static class BuildBrokerProtocol
{
    public const string InstanceHeader = "X-Build-Broker-Instance";
    public const int MaximumRequestBytes = 16 * 1024;
    public const int MaximumLogPageBytes = 64 * 1024;
    // A UTF-16 code unit encodes to at most three UTF-8 bytes; leave room for the newline.
    public const int MaximumLogLineCharacters = MaximumLogPageBytes / 3 - 1;
    public static readonly TimeSpan MaximumLeaseLifetime = TimeSpan.FromMinutes(45);
    // Two independent 1 MiB metadata strings can expand sixfold when the
    // surrounding JSON escapes characters. Keep the wire envelope bounded too.
    public const int MaximumStatusBytes = 16 * 1024 * 1024;
    public const long MaximumArtifactBytes = 256L * 1024 * 1024;

    // The web app and the broker share one 256-bit lowercase hex secret. IO failures
    // propagate so each side keeps its own error handling.
    public static bool TryReadTokenFile(string? path, [NotNullWhen(true)] out string? token)
    {
        token = null;
        if (path is null || !Path.IsPathFullyQualified(path))
            return false;
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget is not null ||
            (file.Attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0 ||
            file.Length is < 64 or > 66)
            return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> bytes = stackalloc byte[67];
        var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        var text = Encoding.ASCII.GetString(bytes[..count]).TrimEnd('\r', '\n');
        if (!BuildPolicy.IsLowerHex(text, 64))
            return false;
        token = text;
        return true;
    }
}

// This is a build protocol, deliberately not a Docker/OCI configuration API.
public sealed record BrokerBuildRequest(string PluginSlug, long BuildId, string GitRepository,
    string? GitRef = null, string? PluginDir = null, string? BuildConfig = null);
public sealed record BrokerBuildAccepted(string LeaseId);
public sealed record BrokerStatus(bool IsReady, string? WorkerImageId, string? ProxyImageId,
    string? UnavailableReason, string InstanceId);
public sealed record BrokerBuildStatus(string State, int NextCursor, string[] Logs,
    BrokerBuildResult? Result, string? Error);
public sealed record BrokerBuildResult(string BuildEnvironmentJson, string ManifestJson,
    string AssemblyName, long ArtifactLength, string ArtifactSha256);
