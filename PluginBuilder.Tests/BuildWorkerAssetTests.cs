using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace PluginBuilder.Tests;

public class BuildWorkerAssetTests
{
    private const string WorkerBaseDigest =
        "sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c";
    private const string ProxyBaseDigest =
        "sha256:8a3baed477e2c282ab8aa5edad442f69873246964f225c5c2ae8364b6610963c";
    private const string PluginPackerCommit = "50ae4bfc5da2e193db5b37fa718dbb92e41958b5";

    [Fact]
    public void WorkerAndProxyPinTrustedInputsAndRunAsFixedNonRootUsers()
    {
        var worker = ReadAsset("PluginBuilder.Dockerfile");
        Assert.Contains(
            $"FROM mcr.microsoft.com/dotnet/sdk:10.0@{WorkerBaseDigest}",
            worker,
            StringComparison.Ordinal);
        Assert.Contains(PluginPackerCommit, worker, StringComparison.Ordinal);
        Assert.Contains("USER 10001:10001", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("openssh", worker, StringComparison.OrdinalIgnoreCase);

        var proxy = ReadAsset("PluginBuilder.Proxy.Dockerfile");
        Assert.Contains(
            $"FROM ubuntu/squid:6.6-24.04_edge@{ProxyBaseDigest}",
            proxy,
            StringComparison.Ordinal);
        Assert.Contains("USER 13:13", proxy, StringComparison.Ordinal);
        Assert.Contains("ENTRYPOINT [\"/usr/sbin/squid\"]", proxy, StringComparison.Ordinal);
        Assert.DoesNotContain("entrypoint.sh", proxy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NuGetConfigurationAllowsOnlyTheOfficialAnonymousFeed()
    {
        var document = XDocument.Load(AssetPath("NuGet.Config"));
        var configuration = Assert.Single(document.Elements("configuration"));

        foreach (var sectionName in new[] { "packageSources", "auditSources" })
        {
            var section = Assert.Single(configuration.Elements(sectionName));
            Assert.NotNull(section.Element("clear"));
            var source = Assert.Single(section.Elements("add"));
            Assert.Equal("nuget.org", source.Attribute("key")?.Value);
            Assert.Equal("https://api.nuget.org/v3/index.json", source.Attribute("value")?.Value);
        }

        var credentials = Assert.Single(configuration.Elements("packageSourceCredentials"));
        Assert.Empty(credentials.Elements());
    }

    [Fact]
    public void ProxyIsConnectOnlyAndAllowsOnlyApprovedPublicHosts()
    {
        var config = ReadAsset("squid.conf");
        Assert.DoesNotContain("ssl_bump", config, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "CONNECT" }, ProxyConnectMethods(config));
        Assert.Contains("acl TLS_port port 443", config, StringComparison.Ordinal);
        Assert.Contains("http_access deny !CONNECT", config, StringComparison.Ordinal);
        Assert.Contains("http_access deny !TLS_port", config, StringComparison.Ordinal);
        Assert.Contains("icp_port 0", config, StringComparison.Ordinal);
        Assert.Contains("htcp_port 0", config, StringComparison.Ordinal);
        Assert.Contains("snmp_port 0", config, StringComparison.Ordinal);
        Assert.Contains("pinger_enable off", config, StringComparison.Ordinal);
        Assert.Contains("cache_mem 16 MB", config, StringComparison.Ordinal);
        Assert.Contains("acl connection_limit maxconn 64", config, StringComparison.Ordinal);
        Assert.Contains("http_access deny connection_limit", config, StringComparison.Ordinal);
        Assert.Contains("delay_parameters 1 12500000/25000000", config, StringComparison.Ordinal);
        Assert.Contains("client_delay_parameters 1 12500000 25000000", config, StringComparison.Ordinal);

        var allowedHosts = AllowedProxyHosts(config);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "github.com",
                "www.github.com",
                "gitlab.com",
                "www.gitlab.com",
                "api.nuget.org",
                "globalcdn.nuget.org",
                "www.nuget.org"
            },
            allowedHosts);

        foreach (var blockedRange in new[]
                 {
                     "10.0.0.0/8",
                     "127.0.0.0/8",
                     "168.63.129.16/32",
                     "169.254.0.0/16",
                     "172.16.0.0/12",
                     "192.168.0.0/16",
                     "::/96",
                     "64:ff9b:1::/48",
                     "fc00::/7",
                     "fe80::/10"
                 })
            Assert.Contains(blockedRange, config, StringComparison.Ordinal);

        var denyUnapproved = config.IndexOf("http_access deny !allowed_destination", StringComparison.Ordinal);
        var denyBlocked = config.IndexOf("http_access deny blocked_ipv4", StringComparison.Ordinal);
        var allowApproved = config.IndexOf(
            "http_access allow CONNECT TLS_port allowed_destination",
            StringComparison.Ordinal);
        var finalDeny = config.LastIndexOf("http_access deny all", StringComparison.Ordinal);
        Assert.True(denyUnapproved >= 0 && denyUnapproved < denyBlocked);
        Assert.True(denyBlocked < allowApproved);
        Assert.True(allowApproved < finalDeny);
    }

    [Theory]
    [InlineData("acl allowed_destination dstdomain -n github.com gitlab.com\n")]
    [InlineData("acl allowed_destination dstdomain -n \\\n    github.com \\\n    gitlab.com\n")]
    [InlineData("acl\tallowed_destination\tdstdomain\t-n\tgithub.com\nacl allowed_destination dstdomain -n gitlab.com\n")]
    public void ProxyAllowlistAssertionsAcceptEquivalentSquidFormatting(string config)
    {
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "github.com", "gitlab.com" },
            AllowedProxyHosts(config));
    }

    [Theory]
    [InlineData("acl CONNECT method CONNECT\n")]
    [InlineData("acl\tCONNECT\tmethod\t\\\n    CONNECT # only tunneling\n")]
    [InlineData("acl CONNECT method CONNECT\nacl CONNECT method CONNECT\n")]
    public void ProxyMethodAssertionsAcceptEquivalentSquidFormatting(string config)
    {
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "CONNECT" }, ProxyConnectMethods(config));
    }

    [Theory]
    [InlineData("acl CONNECT method CONNECT GET\n")]
    [InlineData("acl CONNECT method CONNECT\nacl CONNECT method GET\n")]
    public void ProxyMethodAssertionsRejectAdditionalMethods(string config)
    {
        Assert.False(ProxyConnectMethods(config).SetEquals(["CONNECT"]));
    }

    private static HashSet<string> AllowedProxyHosts(string config) =>
        ProxyAclValues(config, "allowed_destination", "dstdomain")
            .Where(token => token != "-n")
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> ProxyConnectMethods(string config) =>
        ProxyAclValues(config, "CONNECT", "method").ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> ProxyAclValues(string config, string name, string type) =>
        Regex.Replace(config, @"\\\r?\n", " ")
        .Split('\n')
        .Select(line => line.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Where(tokens => tokens.Length >= 4 && tokens[0] == "acl" &&
                         tokens[1] == name && tokens[2] == type)
        .SelectMany(tokens => tokens.Skip(3));

    [Fact]
    public void CloneHelperLocksGitProtocolsAndWorkerConsumesOnlyTrustedOfflineSource()
    {
        var cloneHelper = ReadAsset("clone-source.sh");
        var entrypoint = ReadAsset("entrypoint.sh");

        Assert.Contains("GIT_REPO", cloneHelper, StringComparison.Ordinal);
        Assert.Contains("GIT_REF", cloneHelper, StringComparison.Ordinal);
        Assert.Contains("GIT_TERMINAL_PROMPT=0", cloneHelper, StringComparison.Ordinal);
        Assert.Contains("GIT_CONFIG_VALUE_0=never", cloneHelper, StringComparison.Ordinal);
        Assert.Contains("GIT_CONFIG_VALUE_1=always", cloneHelper, StringComparison.Ordinal);
        Assert.Contains("unset SSH_AUTH_SOCK", cloneHelper, StringComparison.Ordinal);
        Assert.Contains("clone_args=(", cloneHelper, StringComparison.Ordinal);
        Assert.Contains("git \"${clone_args[@]}\"", cloneHelper, StringComparison.Ordinal);
        Assert.Contains("/source/repository", cloneHelper, StringComparison.Ordinal);

        Assert.Contains("exec /usr/bin/perl /limit-output.pl", entrypoint, StringComparison.Ordinal);
        Assert.Contains("BUILD_TIMEOUT_SECONDS", entrypoint, StringComparison.Ordinal);
        Assert.Contains("BUILD_LOG_MAX_BYTES", entrypoint, StringComparison.Ordinal);
        Assert.Contains("BUILD_LOG_MAX_LINE_BYTES", entrypoint, StringComparison.Ordinal);
        Assert.Contains("BUILD_LOG_MAX_LINES", entrypoint, StringComparison.Ordinal);
        Assert.Contains("\"$0\" --run-build", entrypoint, StringComparison.Ordinal);
        Assert.Contains("id -u", entrypoint, StringComparison.Ordinal);
        Assert.Contains("id -g", entrypoint, StringComparison.Ordinal);
        Assert.Contains("[[ -d /source/repository && ! -L /source/repository ]]", entrypoint, StringComparison.Ordinal);
        Assert.Contains("cp --archive --no-preserve=ownership --reflink=never -- /source/repository/.", entrypoint, StringComparison.Ordinal);
        Assert.Contains("--configfile /build-tools/NuGet.Config", entrypoint, StringComparison.Ordinal);
        Assert.DoesNotContain("GIT_REPO", entrypoint, StringComparison.Ordinal);
        Assert.DoesNotContain("GIT_REF", entrypoint, StringComparison.Ordinal);
        Assert.DoesNotContain("git clone", entrypoint, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerRestoresConfigurationConditionalDependenciesForThePublishedConfiguration()
    {
        var entrypoint = ReadAsset("entrypoint.sh");
        var restoreStart = entrypoint.IndexOf(
            "dotnet restore \"$project_file\"",
            StringComparison.Ordinal);
        var publishStart = entrypoint.IndexOf(
            "dotnet publish \"$project_file\"",
            StringComparison.Ordinal);

        Assert.True(restoreStart >= 0, "Could not find the worker's dotnet restore command");
        Assert.True(publishStart > restoreStart, "dotnet publish must run after dotnet restore");
        var restore = entrypoint[restoreStart..publishStart];
        Assert.Contains(
            "--property:Configuration=\"$BUILD_CONFIG\"",
            restore,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet publish \"$project_file\"",
            entrypoint,
            StringComparison.Ordinal);
        Assert.Contains(
            "--configuration \"$BUILD_CONFIG\"",
            entrypoint,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://github.com/owner/repo")]
    [InlineData("https://github.com.evil.test/owner/repo")]
    [InlineData("https://user:password@github.com/owner/repo")]
    [InlineData("https://github.com:443/owner/repo")]
    [InlineData("https://github.com/owner/repo?token=secret")]
    [InlineData("https://gitlab.com/owner/../repo")]
    public async Task CloneHelperRejectsUnsafeRepositoryBeforeInvokingGit(string repository)
    {
        if (OperatingSystem.IsWindows())
            return;

        var result = await RunCloneHelper(repository);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Source checkout rejected:", result.StandardError, StringComparison.Ordinal);
        Assert.False(result.GitWasInvoked);
    }

    [Theory]
    [InlineData("https://github.com/example/plugin")]
    [InlineData("https://gitlab.com/example/subgroup/plugin.git")]
    public async Task CloneHelperAcceptsSupportedHostsAndPassesRefAsAnArgument(string repository)
    {
        if (OperatingSystem.IsWindows())
            return;

        var result = await RunCloneHelper(repository, "release/1.0", fakeGitExitCode: 0);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("clone\n--depth\n1\n--recurse-submodules\n--single-branch\n--branch\nrelease/1.0\n--\n" +
                        repository + "\n", result.GitArguments, StringComparison.Ordinal);
        Assert.Contains("rev-parse\n--verify\nHEAD^{commit}\n", result.GitArguments, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "ExecutorIntegration")]
    public async Task ArtifactStagerExecutesRegularFileAndSizeValidation()
    {
        Assert.True(OperatingSystem.IsLinux(), "This integration test requires a Linux Docker host with runsc.");

        var image = await RunProcess("docker", "image", "inspect", "plugin-builder");
        Assert.True(
            image.ExitCode == 0,
            "The executable stager test requires the prebuilt local 'plugin-builder' image.");

        var valid = await RunArtifactStager(StagerFixture.Valid);
        Assert.Equal(0, valid.ExitCode);
        Assert.Equal(
            ["artifact.btcpay", "artifact.sha256", "build-env.json", "manifest.json"],
            valid.StagedFiles);

        var oversized = await RunArtifactStager(StagerFixture.OversizedManifest);
        Assert.Equal(1, oversized.ExitCode);
        Assert.Contains("plugin manifest exceeds its size limit", oversized.StandardError, StringComparison.Ordinal);
        Assert.Empty(oversized.StagedFiles);

        var oversizedArtifact = await RunArtifactStager(StagerFixture.OversizedArtifact);
        Assert.Equal(1, oversizedArtifact.ExitCode);
        Assert.Contains("plugin artifact exceeds its size limit", oversizedArtifact.StandardError, StringComparison.Ordinal);
        Assert.Empty(oversizedArtifact.StagedFiles);

        var symlink = await RunArtifactStager(StagerFixture.SymlinkManifest);
        Assert.Equal(1, symlink.ExitCode);
        Assert.Contains(
            "plugin manifest is not a regular non-symlink file",
            symlink.StandardError,
            StringComparison.Ordinal);
        Assert.Empty(symlink.StagedFiles);
    }

    [Fact]
    public async Task OutputLimiterPassesThroughOutputAndChildExitCode()
    {
        if (OperatingSystem.IsWindows())
            return;

        var result = await RunLimiter(
            1024,
            256,
            20,
            5,
            "/bin/sh",
            "-c",
            "printf 'normal\\n'; printf 'warning\\n' >&2; exit 23");

        Assert.Equal(23, result.ExitCode);
        Assert.Equal("normal\nwarning\n", result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Theory]
    [InlineData(5, 100, 10, "123456", "12345")]
    [InlineData(100, 5, 10, "123456", "12345")]
    [InlineData(100, 100, 2, "a\nb\nc\n", "a\nb\n")]
    public async Task OutputLimiterStopsBuildAtEveryOutputBoundary(
        int maxBytes,
        int maxLineBytes,
        int maxLines,
        string childOutput,
        string expectedOutput)
    {
        if (OperatingSystem.IsWindows())
            return;

        var result = await RunLimiter(
            maxBytes,
            maxLineBytes,
            maxLines,
            5,
            "/usr/bin/printf",
            "%s",
            childOutput);

        Assert.Equal(78, result.ExitCode);
        Assert.Equal(expectedOutput, result.StandardOutput);
    }

    [Fact]
    public async Task OutputLimiterTimesOutAndKillsSilentBuild()
    {
        if (OperatingSystem.IsWindows())
            return;

        var stopwatch = Stopwatch.StartNew();
        var result = await RunLimiter(1024, 256, 20, 1, "/bin/sh", "-c", "sleep 30");
        stopwatch.Stop();

        Assert.Equal(124, result.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Limiter took {stopwatch.Elapsed}");
    }

    [LinuxFact]
    public async Task OuterLimiterCapsOutputThatBypassesInnerLimiterThroughItsProcessFileDescriptor()
    {
        const int outerLimit = 4096;
        var result = await RunLimiter(
            outerLimit,
            outerLimit,
            10,
            5,
            "/usr/bin/perl",
            AssetPath("limit-output.pl"),
            "2097152",
            "2097152",
            "10",
            "5",
            "--",
            "/bin/sh",
            "-c",
            "head -c 1048576 /dev/zero > /proc/$PPID/fd/1");

        Assert.Equal(78, result.ExitCode);
        Assert.Equal(outerLimit, result.StandardOutput.Length);
    }

    private static async Task<CloneHelperResult> RunCloneHelper(
        string repository, string gitRef = "", int fakeGitExitCode = 73)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"plugin-builder-clone-helper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var calledPath = Path.Combine(directory, "git-called");
        var fakeGit = Path.Combine(directory, "git");
        var fakeId = Path.Combine(directory, "id");
        var source = Directory.CreateDirectory(Path.Combine(directory, "source")).FullName;
        var helper = Path.Combine(directory, "clone-source.sh");

        try
        {
            await File.WriteAllTextAsync(fakeGit, """
                #!/bin/sh
                printf '%s\n' "$@" >> "${PB_FAKE_GIT_CALLED:?}"
                exit "${PB_FAKE_GIT_EXIT_CODE:?}"
                """);
            // Rebase only filesystem locations so the production shell validation and
            // git argument construction can run without writing host /source or /tmp/home.
            await File.WriteAllTextAsync(helper, ReadAsset("clone-source.sh")
                .Replace("/source", source, StringComparison.Ordinal)
                .Replace("/tmp/home", Path.Combine(directory, "home"), StringComparison.Ordinal));
            await File.WriteAllTextAsync(fakeId, """
                #!/bin/sh
                case "${1:-}" in
                    -u|-g) printf '%s\n' 10002 ;;
                    *) exit 2 ;;
                esac
                """);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    fakeGit,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(
                    fakeId,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/env",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            process.StartInfo.ArgumentList.Add("bash");
            process.StartInfo.ArgumentList.Add(helper);
            process.StartInfo.Environment["PATH"] =
                directory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            process.StartInfo.Environment["PB_FAKE_GIT_CALLED"] = calledPath;
            process.StartInfo.Environment["PB_FAKE_GIT_EXIT_CODE"] = fakeGitExitCode.ToString();
            process.StartInfo.Environment["GIT_REPO"] = repository;
            process.StartInfo.Environment["GIT_REF"] = gitRef;

            process.Start();
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            return new CloneHelperResult(
                process.ExitCode,
                await standardOutput,
                await standardError,
                File.Exists(calledPath),
                File.Exists(calledPath) ? await File.ReadAllTextAsync(calledPath) : string.Empty);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<ProcessResult> RunLimiter(
        int maxBytes,
        int maxLineBytes,
        int maxLines,
        int timeoutSeconds,
        string command,
        params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/perl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in new[]
                 {
                     AssetPath("limit-output.pl"),
                     maxBytes.ToString(),
                     maxLineBytes.ToString(),
                     maxLines.ToString(),
                     timeoutSeconds.ToString(),
                     "--",
                     command
                 }.Concat(arguments))
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds + 5));
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static async Task<StagerResult> RunArtifactStager(StagerFixture fixture)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The executable stager test requires a Unix Docker host");

        var directory = Path.Combine(Path.GetTempPath(), $"plugin-builder-stager-{Guid.NewGuid():N}");
        var source = Path.Combine(directory, "source");
        var output = Path.Combine(directory, "output");
        var staging = Path.Combine(directory, "staging");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(staging);

        try
        {
            // A linked worktree's .git file points outside the container mount.
            // Give the fixture its own repository, independent of the test checkout.
            var init = await RunProcess("git", "init", "--quiet", "--template=", "--shared=all", source);
            Assert.Equal(0, init.ExitCode);
            var commit = await RunProcess("git", "-C", source,
                "-c", "core.hooksPath=/dev/null", "-c", "user.name=Stager test",
                "-c", "user.email=stager@example.invalid", "commit", "--quiet",
                "--no-gpg-sign", "--allow-empty", "-m", "Stager fixture");
            Assert.Equal(0, commit.ExitCode);
            var provenance = await RunProcess("git", "-C", source, "show", "-s", "--format=%H%n%cI", "HEAD");
            Assert.Equal(0, provenance.ExitCode);
            var expectedProvenance = provenance.StandardOutput.Trim().Split('\n');

            const string assemblyName = "Example";
            var artifact = Path.Combine(output, $"{assemblyName}.btcpay");
            var artifactBytes = "artifact"u8.ToArray();
            await File.WriteAllBytesAsync(artifact, artifactBytes);
            var manifest = Path.Combine(output, $"{assemblyName}.btcpay.json");
            await File.WriteAllTextAsync(Path.Combine(output, "build-env.json"),
                """{"gitCommit":"forged-commit","gitCommitDate":"forged-date","buildHash":"forged-hash"}""");

            switch (fixture)
            {
                case StagerFixture.Valid:
                    await File.WriteAllTextAsync(manifest, "{}");
                    // Staged permissions must be normalized independently of the inputs.
                    File.SetUnixFileMode(manifest, UnixFileMode.UserRead |
                                                   UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                    File.SetUnixFileMode(artifact, UnixFileMode.UserRead | UnixFileMode.UserExecute |
                                                   UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                    break;
                case StagerFixture.OversizedManifest:
                    await File.WriteAllTextAsync(
                        manifest,
                        $"{{\"padding\":\"{new string('a', 1024 * 1024)}\"}}");
                    break;
                case StagerFixture.OversizedArtifact:
                    await File.WriteAllTextAsync(manifest, "{}");
                    await using (var oversizedArtifact = new FileStream(
                                     artifact,
                                     FileMode.Create,
                                     FileAccess.Write))
                        oversizedArtifact.SetLength(256L * 1024 * 1024 + 1);
                    break;
                case StagerFixture.SymlinkManifest:
                    const string targetName = "manifest-target.json";
                    await File.WriteAllTextAsync(Path.Combine(output, targetName), "{}");
                    File.CreateSymbolicLink(manifest, targetName);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(fixture), fixture, null);
            }

            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.SetUnixFileMode(
                output,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                         UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                         UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                              UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                              UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

            var result = await RunProcess(
                "docker",
                "run", "--rm", "--pull", "never", "--runtime", "runsc", "--network", "none",
                "--user", "10001:10001",
                "--mount", $"type=bind,source={AssetPath("stage-artifacts.sh")},target=/stage-artifacts.sh,readonly",
                "--mount", $"type=bind,source={output},target=/untrusted-output,readonly",
                "--mount", $"type=bind,source={source},target=/source/repository,readonly",
                "--mount", $"type=bind,source={staging},target=/staging",
                "--entrypoint", "/stage-artifacts.sh",
                "plugin-builder");

            if (result.ExitCode == 0)
            {
                foreach (var file in Directory.EnumerateFiles(staging))
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            }

            if (result.ExitCode == 0 && fixture == StagerFixture.Valid)
            {
                Assert.Equal(artifactBytes,
                    await File.ReadAllBytesAsync(Path.Combine(staging, "artifact.btcpay")));
                var expectedHash = Convert.ToHexString(SHA256.HashData(artifactBytes)).ToLowerInvariant();
                Assert.Equal(expectedHash,
                    (await File.ReadAllTextAsync(Path.Combine(staging, "artifact.sha256"))).Trim());
                var buildEnvironment = JObject.Parse(
                    await File.ReadAllTextAsync(Path.Combine(staging, "build-env.json")));
                Assert.Equal(expectedHash, buildEnvironment["buildHash"]?.Value<string>());
                Assert.Equal(assemblyName, buildEnvironment["assemblyName"]?.Value<string>());
                Assert.Equal(expectedProvenance[0], buildEnvironment["gitCommit"]?.Value<string>());
                Assert.Equal(DateTimeOffset.Parse(expectedProvenance[1]),
                    DateTimeOffset.Parse(buildEnvironment["gitCommitDate"]!.Value<string>()!));
                Assert.True(DateTimeOffset.TryParse(buildEnvironment["buildDate"]?.Value<string>(), out _));
            }

            return new StagerResult(
                result.ExitCode,
                result.StandardError,
                Directory.GetFiles(staging).Select(Path.GetFileName).Order().ToArray()!);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<ProcessResult> RunProcess(string fileName, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static string ReadAsset(string name) => File.ReadAllText(AssetPath(name));

    private static string AssetPath(string name) => Path.Combine(ProjectDirectory, name);

    private static string ProjectDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var project = Path.Combine(directory.FullName, "PluginBuilder", "PluginBuilder.csproj");
                if (File.Exists(project))
                    return Path.GetDirectoryName(project)!;

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not find the PluginBuilder project directory");
        }
    }

    private sealed record CloneHelperResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool GitWasInvoked,
        string GitArguments);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record StagerResult(int ExitCode, string StandardError, string[] StagedFiles);

    private enum StagerFixture
    {
        Valid,
        OversizedManifest,
        OversizedArtifact,
        SymlinkManifest
    }
}
