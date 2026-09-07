using PluginBuilder.Services;
using PluginBuilder.Util;
using Xunit;

namespace PluginBuilder.Tests;

public class BuildSandboxContractTests
{
    [Fact]
    public void MaximumConcurrencyIsTwo()
    {
        Assert.Equal(2, DockerBuildSandbox.MaxConcurrentBuilds);
    }

    [Theory]
    [InlineData("https://github.com/owner/repository")]
    [InlineData("https://www.github.com/owner/repository")]
    [InlineData("https://github.com:443/owner/repository.git")]
    [InlineData("https://gitlab.com/group/subgroup/repository")]
    [InlineData("https://www.gitlab.com/group/subgroup/repository")]
    [InlineData("https://GITHUB.COM/owner/repository")]
    public void RepositoryValidationAcceptsOnlyApprovedAnonymousHttpsUrls(string repository)
    {
        DockerBuildSandbox.ValidateRepositoryUrl(repository);
    }

    [Fact]
    public void RepositoryNormalizationProducesTheExactFormAcceptedByTheWorker()
    {
        Assert.Equal(
            "https://github.com/Owner/Repository.git",
            DockerBuildSandbox.NormalizeRepositoryUrl(
                "HTTPS://GITHUB.COM:443/Owner/Repository.git/"));
        Assert.Equal(
            "https://gitlab.com/group/subgroup/repository",
            DockerBuildSandbox.NormalizeRepositoryUrl(
                "https://GitLab.Com/group/subgroup/repository"));
        Assert.Equal(
            "https://github.com/owner/repository",
            DockerBuildSandbox.NormalizeRepositoryUrl(
                "https://www.github.com/owner/repository"));
        Assert.Equal(
            "https://gitlab.com/group/repository",
            DockerBuildSandbox.NormalizeRepositoryUrl(
                "https://www.gitlab.com/group/repository"));
    }

    [Theory]
    [InlineData("http://github.com/owner/repository")]
    [InlineData("ssh://git@github.com/owner/repository")]
    [InlineData("git@github.com:owner/repository.git")]
    [InlineData("https://github.com.evil.test/owner/repository")]
    [InlineData("https://gitlab.example.com/owner/repository")]
    [InlineData("https://user:password@github.com/owner/repository")]
    [InlineData("https://github.com:8443/owner/repository")]
    [InlineData("https://github.com/owner/repository?token=secret")]
    [InlineData("https://github.com/owner/repository#fragment")]
    [InlineData("https://127.0.0.1/owner/repository")]
    [InlineData("https://github.com/")]
    [InlineData("https://github.com/owner")]
    [InlineData("https://github.com/owner/%2e%2e/repository")]
    [InlineData("https://github.com/owner/repository%0aevil")]
    [InlineData("https://gith\u00fcb.com/owner/repository")]
    [InlineData("not a URL")]
    public void RepositoryValidationRejectsEveryOtherEndpoint(string repository)
    {
        Assert.Throws<BuildServiceException>(() =>
            DockerBuildSandbox.ValidateRepositoryUrl(repository));
    }

    [Fact]
    public void WorkerUsesOnlyItsInternalNetworkAndNumericProxyWithStrictResourceLimits()
    {
        const string workerImageId =
            "sha256:1111111111111111111111111111111111111111111111111111111111111111";
        var arguments = DockerBuildSandbox.CreateWorkerArguments(
            "pb-worker-test",
            "pb-internal-test",
            "172.31.0.2",
            "/scratch/test/source",
            "/scratch/test/work",
            "/scratch/test/output",
            workerImageId,
            new FullBuildId("example-plugin", 42),
            new BuildInfo
            {
                GitRepository = "https://github.com/example/plugin",
                GitRef = "main",
                PluginDir = "src/Plugin",
                BuildConfig = "Release"
            },
            TimeSpan.FromMinutes(15));

        Assert.Equal(["container", "create"], arguments.Take(2));
        AssertOption(arguments, "--name", "pb-worker-test");
        AssertOption(arguments, "--label", "BTCPAY_PLUGIN_BUILD=example-plugin/42");
        Assert.Equal(1, arguments.Count(argument => argument == "--network"));
        AssertOption(arguments, "--network", "pb-internal-test");
        Assert.DoesNotContain("pb-egress-test", arguments);
        AssertOption(arguments, "--runtime", "runsc");
        Assert.Contains("--read-only", arguments);
        AssertOption(arguments, "--user", "10001:10001");
        AssertOption(arguments, "--cap-drop", "ALL");
        AssertOption(arguments, "--security-opt", "no-new-privileges:true");
        Assert.DoesNotContain("--privileged", arguments);
        Assert.DoesNotContain("--cap-add", arguments);
        AssertOption(arguments, "--memory", "3g");
        AssertOption(arguments, "--memory-swap", "3g");
        AssertOption(arguments, "--cpus", "2");
        AssertOption(arguments, "--pids-limit", "512");
        AssertOption(arguments, "--ulimit", "nofile=4096:4096");
        AssertOption(arguments, "--log-driver", "none");
        Assert.Equal(["192.0.2.1"], OptionValues(arguments, "--dns"));
        Assert.Equal(["timeout:1", "attempts:1"], OptionValues(arguments, "--dns-option"));

        AssertOption(
            arguments,
            "--mount",
            "type=bind,source=/scratch/test/source,target=/source,readonly");
        AssertOption(arguments, "--mount", "type=bind,source=/scratch/test/work,target=/build");
        Assert.Contains(
            "type=bind,source=/scratch/test/output,target=/out",
            OptionValues(arguments, "--mount"));

        var environment = OptionValues(arguments, "--env").ToArray();
        foreach (var expected in new[]
                 {
                     "HTTP_PROXY=http://172.31.0.2:3128",
                     "HTTPS_PROXY=http://172.31.0.2:3128",
                     "http_proxy=http://172.31.0.2:3128",
                     "https_proxy=http://172.31.0.2:3128",
                     "ALL_PROXY=",
                     "all_proxy=",
                     "NO_PROXY=",
                     "no_proxy=",
                     "BUILD_TIMEOUT_SECONDS=900",
                     $"BUILD_LOG_MAX_BYTES={DockerBuildSandbox.MaxBuildLogBytes}",
                     $"BUILD_LOG_MAX_LINE_BYTES={DockerBuildSandbox.MaxBuildLogLineBytes}",
                     $"BUILD_LOG_MAX_LINES={DockerBuildSandbox.MaxBuildLogLines}"
                 })
            Assert.Contains(expected, environment);

        Assert.DoesNotContain(
            environment,
            variable => variable.StartsWith("AZURE", StringComparison.OrdinalIgnoreCase) ||
                        variable.StartsWith("PB_", StringComparison.OrdinalIgnoreCase) ||
                        variable.Contains("STORAGE", StringComparison.OrdinalIgnoreCase) ||
                        variable.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) ||
                        variable.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
                        variable.Contains("CONNECTION_STRING", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            environment,
            variable => variable.StartsWith("GIT_REPO=", StringComparison.Ordinal) ||
                        variable.StartsWith("GIT_REF=", StringComparison.Ordinal));
        Assert.DoesNotContain(
            arguments,
            argument => argument.Contains("docker.sock", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(workerImageId, arguments[^1]);
    }

    private static IEnumerable<string> OptionValues(IReadOnlyList<string> arguments, string option)
    {
        for (var i = 0; i < arguments.Count - 1; i++)
            if (arguments[i] == option)
                yield return arguments[i + 1];
    }

    private static void AssertOption(IReadOnlyList<string> arguments, string option, string expectedValue)
    {
        Assert.Contains(expectedValue, OptionValues(arguments, option));
    }
}
