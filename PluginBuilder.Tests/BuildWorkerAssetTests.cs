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
        var worker = ReadAsset(Path.Combine("..", "Dockerfile.worker"));
        Assert.Contains(
            $"FROM mcr.microsoft.com/dotnet/sdk:10.0@{WorkerBaseDigest}",
            worker,
            StringComparison.Ordinal);
        Assert.Contains(PluginPackerCommit, worker, StringComparison.Ordinal);
        Assert.Contains("USER 10001:10001", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("openssh", worker, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK=true",
            worker, StringComparison.Ordinal);

        var proxy = ReadAsset(Path.Combine("..", "Dockerfile.proxy"));
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
    public void WorkerBuildsPreparedSourceWithoutCloning()
    {
        var entrypoint = ReadAsset("entrypoint.sh");

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
    public async Task CloneHelperLocksGitEnvironmentAndPassesRepositoryAndRefAsArguments(string repository)
    {
        if (OperatingSystem.IsWindows())
            return;

        var result = await RunCloneHelper(repository, "release/1.0", fakeGitExitCode: 0);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("check-ref-format\n--branch\nrelease/1.0\n" +
                     "clone\n--depth\n1\n--recurse-submodules\n--shallow-submodules\n--single-branch\n--branch\nrelease/1.0\n--\n" +
                     repository + "\n/source/repository\n-C\n/source/repository\nrev-parse\n--verify\nHEAD^{commit}\n",
            result.GitArguments);
        Assert.Equal(new[]
        {
            "GIT_TERMINAL_PROMPT=0", "GIT_ASKPASS=/bin/false", "SSH_ASKPASS=/bin/false", "GCM_INTERACTIVE=Never",
            "GIT_CONFIG_NOSYSTEM=1", "GIT_CONFIG_GLOBAL=/dev/null", "SSH_AUTH_SOCK=<unset>",
            "GIT_CONFIG_COUNT=2", "GIT_CONFIG_KEY_0=protocol.allow", "GIT_CONFIG_VALUE_0=never",
            "GIT_CONFIG_KEY_1=protocol.https.allow", "GIT_CONFIG_VALUE_1=always"
        }, result.GitEnvironment);
    }

    [LinuxFact]
    [Trait("Category", "ExecutorIntegration")]
    public async Task ArtifactStagerExecutesRegularFileAndSizeValidation()
    {
        var image = await RunProcess("docker", "image", "inspect", "plugin-builder-worker");
        Assert.True(
            image.ExitCode == 0,
            "The executable stager test requires the prebuilt local 'plugin-builder-worker' image.");

        var valid = await RunArtifactStager(StagerFixture.Valid);
        Assert.Equal(0, valid.ExitCode);
        Assert.Equal(
            ["artifact.btcpay", "build-env.json", "manifest.json"],
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

    [LinuxFact]
    public async Task ProcessTimeoutTerminatesTheStartedProcess()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("/bin/sleep", "30")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            }
        };
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => RunProcess(process, TimeSpan.FromMilliseconds(100)));
            Assert.True(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static async Task<CloneHelperResult> RunCloneHelper(
        string repository, string gitRef = "", int fakeGitExitCode = 73)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"plugin-builder-clone-helper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var calledPath = Path.Combine(directory, "git-called");
        var environmentPath = Path.Combine(directory, "git-environment");
        var fakeGit = Path.Combine(directory, "git");
        var fakeId = Path.Combine(directory, "id");
        var source = Directory.CreateDirectory(Path.Combine(directory, "source")).FullName;
        var helper = Path.Combine(directory, "clone-source.sh");

        try
        {
            await File.WriteAllTextAsync(fakeGit, """
                #!/bin/sh
                printf '%s\n' "$@" >> "${PB_FAKE_GIT_CALLED:?}"
                if [ "${1:-}" = clone ]; then
                    for name in GIT_TERMINAL_PROMPT GIT_ASKPASS SSH_ASKPASS GCM_INTERACTIVE \
                        GIT_CONFIG_NOSYSTEM GIT_CONFIG_GLOBAL SSH_AUTH_SOCK GIT_CONFIG_COUNT \
                        GIT_CONFIG_KEY_0 GIT_CONFIG_VALUE_0 GIT_CONFIG_KEY_1 GIT_CONFIG_VALUE_1; do
                        printf '%s=%s\n' "$name" "$(printenv "$name" || printf '<unset>')"
                    done > "${PB_FAKE_GIT_ENVIRONMENT:?}"
                fi
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
            process.StartInfo.Environment["PB_FAKE_GIT_ENVIRONMENT"] = environmentPath;
            process.StartInfo.Environment["PB_FAKE_GIT_EXIT_CODE"] = fakeGitExitCode.ToString();
            // Prove the checkout overrides inherited credentials/config, rather than
            // relying on the test runner already having a restricted environment.
            foreach (var name in new[] { "GIT_TERMINAL_PROMPT", "GIT_ASKPASS", "SSH_ASKPASS", "GCM_INTERACTIVE",
                         "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_GLOBAL", "SSH_AUTH_SOCK", "GIT_CONFIG_COUNT",
                         "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0", "GIT_CONFIG_KEY_1", "GIT_CONFIG_VALUE_1" })
                process.StartInfo.Environment[name] = "inherited-untrusted";
            process.StartInfo.Environment["GIT_REPO"] = repository;
            process.StartInfo.Environment["GIT_REF"] = gitRef;

            var result = await RunProcess(process, TimeSpan.FromSeconds(5));

            return new CloneHelperResult(
                result.ExitCode,
                result.StandardOutput,
                result.StandardError,
                File.Exists(calledPath),
                File.Exists(calledPath)
                    ? (await File.ReadAllTextAsync(calledPath)).Replace(source, "/source", StringComparison.Ordinal)
                    : string.Empty,
                File.Exists(environmentPath) ? await File.ReadAllLinesAsync(environmentPath) : []);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<StagerResult> RunArtifactStager(StagerFixture fixture)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The executable stager test requires a Unix Docker host");

        var directory = Path.Combine(Path.GetTempPath(), $"plugin-builder-stager-{Guid.NewGuid():N}");
        var container = $"pb-stager-test-{Guid.NewGuid():N}";
        var readerContainer = $"{container}-reader";
        Exception? executionFailure = null;
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
                "run", "--rm", "--name", container, "--pull", "never", "--runtime", "runsc", "--network", "none",
                "--user", "10001:10001",
                "--mount", $"type=bind,source={AssetPath("stage-artifacts.sh")},target=/stage-artifacts.sh,readonly",
                "--mount", $"type=bind,source={output},target=/untrusted-output,readonly",
                "--mount", $"type=bind,source={source},target=/source/repository,readonly",
                "--mount", $"type=bind,source={staging},target=/staging",
                "--entrypoint", "/stage-artifacts.sh",
                "plugin-builder-worker");

            if (result.ExitCode == 0)
            {
                foreach (var file in Directory.EnumerateFiles(staging))
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            }

            if (result.ExitCode == 0 && fixture == StagerFixture.Valid)
            {
                var expectedHash = Convert.ToHexString(SHA256.HashData(artifactBytes)).ToLowerInvariant();
                // The stager deliberately creates uid 10001-owned files with mode 0600.
                // Read them as that uid so this test also works for a non-root host user.
                var stagedContents = await RunProcess(
                    "docker",
                    "run", "--rm", "--name", readerContainer, "--pull", "never", "--runtime", "runsc",
                    "--network", "none", "--user", "10001:10001",
                    "--mount", $"type=bind,source={staging},target=/staging,readonly",
                    "--entrypoint", "/bin/sh", "plugin-builder-worker",
                    "-c", "sha256sum /staging/artifact.btcpay && cat /staging/build-env.json");
                Assert.Equal(0, stagedContents.ExitCode);
                var outputLines = stagedContents.StandardOutput.Split('\n', 2);
                Assert.Equal(2, outputLines.Length);
                Assert.StartsWith(expectedHash + "  /staging/artifact.btcpay", outputLines[0], StringComparison.Ordinal);
                var buildEnvironment = JObject.Parse(outputLines[1]);
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
        catch (Exception failure)
        {
            executionFailure = failure;
            throw;
        }
        finally
        {
            try
            {
                // Killing the Docker client on timeout does not stop its container.
                foreach (var containerToRemove in new[] { container, readerContainer })
                {
                    var cleanup = await RunProcess("docker", "rm", "--force", containerToRemove);
                    Assert.True(cleanup.ExitCode == 0 ||
                                cleanup.StandardError.Contains("No such container", StringComparison.OrdinalIgnoreCase),
                        $"Could not remove test container {containerToRemove}: {cleanup.StandardError}");
                }
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception cleanupFailure) when (executionFailure is not null)
            {
                throw new AggregateException("Artifact staging and test cleanup both failed.", executionFailure, cleanupFailure);
            }
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

        return await RunProcess(process, TimeSpan.FromSeconds(30));
    }

    private static async Task<ProcessResult> RunProcess(Process process, TimeSpan timeout)
    {
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
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
        string GitArguments,
        string[] GitEnvironment);

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
