using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace PluginBuilder.Builds.Services;

/// <summary>Shared build inputs and bounds; no Docker execution or application dependencies.</summary>
public static class BuildPolicy
{
    public const int MaxConcurrentBuilds = 2;
    public const int WorkerPidLimit = 512;
    public const int MaxBuildMetadataBytes = 1024 * 1024;
    public const int MaxBuildLogBytes = 10 * 1024 * 1024;
    public const int MaxBuildLogLines = 10_000;
    private const int MaxRepositoryUrlCharacters = 2048;
    private const int MaxGitRefCharacters = 255;
    private const int MaxPluginDirectoryCharacters = 1024;
    private static readonly Regex SafeAssemblyName = new(
        "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Staged build metadata is validated on both sides of the broker boundary.
    public static bool IsLowerHex([NotNullWhen(true)] string? value, int length) =>
        value is not null && value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool IsSha256Hex([NotNullWhen(true)] string? value) => IsLowerHex(value, 64);
    public static bool IsGitObjectId([NotNullWhen(true)] string? value) => IsLowerHex(value, 40) || IsLowerHex(value, 64);
    public static bool IsSafeAssemblyName([NotNullWhen(true)] string? value) => value is not null && SafeAssemblyName.IsMatch(value);

    public static string NormalizeRepositoryUrl(string repository)
    {
        if (string.IsNullOrEmpty(repository) || repository.Length > MaxRepositoryUrlCharacters ||
            !Uri.TryCreate(repository, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            (!uri.IsDefaultPort && uri.Port != 443) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 ||
            uri.IdnHost is not ("github.com" or "www.github.com" or "gitlab.com" or "www.gitlab.com"))
            throw new BuildServiceException("Git repository must be an anonymous HTTPS URL on github.com or gitlab.com.");

        var path = uri.AbsolutePath.TrimEnd('/');
        var pathSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathSegments.Length < 2 ||
            !Regex.IsMatch(path, "\\A/[A-Za-z0-9._/-]+\\z", RegexOptions.CultureInvariant) ||
            path.Contains("//", StringComparison.Ordinal) || pathSegments.Any(segment => segment is "." or ".."))
            throw new BuildServiceException("Git repository must contain a safe owner and repository path.");

        // Use the same canonical form as the worker's independent validation.
        var canonicalHost = uri.IdnHost switch
        {
            "www.github.com" => "github.com",
            "www.gitlab.com" => "gitlab.com",
            _ => uri.IdnHost
        };
        return $"https://{canonicalHost}{path}";
    }

    public static void ValidateBuildInputs(string? gitRef, string? pluginDirectory, string? buildConfig)
    {
        // A leading '-' would be parsed by git as an option, not a ref.
        if (!string.IsNullOrEmpty(gitRef) && (gitRef.Length > MaxGitRefCharacters || gitRef.Any(char.IsControl) ||
                                              gitRef.StartsWith('-') || gitRef.Contains('\\')))
            throw new BuildServiceException("Git ref is too long, starts with '-' or contains control characters or backslashes.");
        if (!string.IsNullOrEmpty(pluginDirectory))
        {
            var segments = pluginDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (pluginDirectory.Length > MaxPluginDirectoryCharacters || pluginDirectory.StartsWith('/') ||
                pluginDirectory.Any(char.IsControl) || pluginDirectory.Contains('\\') ||
                segments.Any(segment => segment is "." or ".."))
                throw new BuildServiceException("Plugin directory is not a safe relative path.");
        }
        if (!string.IsNullOrEmpty(buildConfig) &&
            !Regex.IsMatch(buildConfig, "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\\z", RegexOptions.CultureInvariant))
            throw new BuildServiceException("Build configuration is invalid.");
    }
}
