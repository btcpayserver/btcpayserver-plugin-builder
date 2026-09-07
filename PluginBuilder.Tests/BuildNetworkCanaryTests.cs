using System.Diagnostics;
using PluginBuilder.Services;
using PluginBuilder.Util;
using Xunit;

namespace PluginBuilder.Tests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class BuildNetworkCanaryTests
{
    [Fact]
    [Trait("Category", "ExecutorIntegration")]
    public async Task RunscWorkerCanReachOnlyProxyAndDeniedHostsDoNotReachDns()
    {
        Assert.True(OperatingSystem.IsLinux(), "The network canary requires a Linux Docker host with runsc.");
        var id = Guid.NewGuid().ToString("N");
        var label = $"BTCPAY_PLUGIN_BUILD=network-canary-{id}";
        var egress = $"pb-canary-egress-{id}";
        var isolated = $"pb-canary-internal-{id}";
        var fixture = $"pb-canary-fixture-{id}";
        var proxy = $"pb-canary-proxy-{id}";
        var worker = $"pb-canary-worker-{id}";
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pb-canary-{id}")).FullName;
        var helper = Path.Combine(AppContext.BaseDirectory, "network-canary-fixture");
        Assert.True(File.Exists(Path.Combine(helper, "NetworkCanary.dll")), "Build the test project and its canary fixture first.");

        try
        {
            await Docker("image", "inspect", "plugin-builder", "plugin-builder-proxy");
            // Fixture egress deliberately has no Internet route. A permissive ACL
            // regression must fail the test without contacting a real destination.
            await Docker("network", "create", "--internal", "--ipv6=false", "--label", label, egress);
            await Docker(DockerBuildSandbox.CreateInternalNetworkArguments(isolated, label).ToArray());
            await Docker("run", "--detach", "--pull", "never", "--name", fixture, "--label", label,
                "--runtime", "runsc", "--network", egress, "--read-only", "--user", "0:0",
                "--cap-drop", "ALL", "--cap-add", "NET_BIND_SERVICE", "--security-opt", "no-new-privileges:true",
                "--memory", "128m", "--memory-swap", "128m", "--cpus", "0.5", "--pids-limit", "64",
                "--log-opt", "max-size=1m", "--log-opt", "max-file=1",
                "--mount", $"type=bind,source={helper},target=/canary,readonly",
                "--entrypoint", "dotnet", "plugin-builder", "/canary/NetworkCanary.dll", "serve");
            var fixtureIp = await Address(fixture, egress);
            await WaitReady(fixture, "READY=8080");
            var control = await Docker("exec", fixture, "dotnet", "/canary/NetworkCanary.dll", "control", fixtureIp);
            Assert.Contains("CONTROL=PASS", control, StringComparison.Ordinal);

            var resolver = Path.Combine(directory, "resolv.conf");
            await File.WriteAllTextAsync(resolver, $"nameserver {fixtureIp}\noptions timeout:1 attempts:1\n");
            await Docker(DockerBuildSandbox.CreateProxyArguments(proxy, egress, resolver, "plugin-builder-proxy", label).ToArray());
            await Docker("network", "connect", isolated, proxy);
            await Docker("start", proxy);
            await WaitProxyReady(proxy);
            var proxyIp = await Address(proxy, isolated);

            var source = Directory.CreateDirectory(Path.Combine(directory, "source")).FullName;
            var work = Directory.CreateDirectory(Path.Combine(directory, "work")).FullName;
            var output = Directory.CreateDirectory(Path.Combine(directory, "output")).FullName;
            var arguments = DockerBuildSandbox.CreateWorkerArguments(worker, isolated, proxyIp, source, work, output,
                "plugin-builder", new FullBuildId("network-canary", 1), new BuildInfo(), TimeSpan.FromSeconds(30)).ToList();
            // Keep the actual worker network/runtime/resource/secret policy. Only
            // replace its build program with the benign, read-only probe executable.
            arguments.InsertRange(arguments.Count - 1,
                ["--mount", $"type=bind,source={helper},target=/canary,readonly", "--entrypoint", "dotnet"]);
            arguments.AddRange(["/canary/NetworkCanary.dll", "probe", proxyIp, fixtureIp]);
            await Docker(arguments.ToArray());
            Assert.Equal("runsc", (await Docker("inspect", "--format", "{{.HostConfig.Runtime}}", worker)).Trim());
            var probe = await Docker("start", "--attach", worker);
            Assert.Contains("NETWORK_CANARY=PASS", probe, StringComparison.Ordinal);

            var dnsLog = await Docker("logs", fixture);
            // Include every query so numeric CONNECT targets cannot leak through
            // reverse DNS when the proxy's no-lookup domain ACL regresses.
            var queriedNames = dnsLog.Split('\n')
                .Where(line => line.StartsWith("DNS=", StringComparison.Ordinal))
                .Select(line => line[4..].TrimEnd('\r'))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(
                ["api.nuget.org", "control.canary.test", "github.com", "gitlab.com"],
                queriedNames);
            foreach (var port in new[] { 443, 5432, 8080 })
                Assert.Single(dnsLog.Split('\n'), line => line == $"CONNECTION={port}");
        }
        finally
        {
            var cleanupErrors = new List<string>();
            foreach (var container in new[] { worker, proxy, fixture })
            {
                var result = await RunDocker("rm", "--force", container);
                if (result.Code != 0 && !result.Output.Contains("No such container", StringComparison.OrdinalIgnoreCase))
                    cleanupErrors.Add(result.Output);
            }
            foreach (var network in new[] { isolated, egress })
            {
                var result = await RunDocker("network", "rm", network);
                if (result.Code != 0 && !result.Output.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    cleanupErrors.Add(result.Output);
            }
            Assert.Empty(cleanupErrors);
            Directory.Delete(directory, recursive: true);
        }
        Assert.Empty((await Docker("ps", "-aq", "--filter", $"label={label}")).Trim());
        Assert.Empty((await Docker("network", "ls", "-q", "--filter", $"label={label}")).Trim());
    }

    private static async Task<string> Address(string container, string network) =>
        (await Docker("inspect", "--format", $"{{{{(index .NetworkSettings.Networks \"{network}\").IPAddress}}}}", container)).Trim();

    private static async Task WaitReady(string container, string marker)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if ((await Docker("logs", container)).Contains(marker, StringComparison.Ordinal))
                return;
            await Task.Delay(200);
        }
        Assert.Fail("Canary fixture did not become ready: " + await Docker("logs", container));
    }

    private static async Task WaitProxyReady(string container)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if ((await RunDocker("exec", container, "/bin/bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/3128")).Code == 0)
                return;
            await Task.Delay(200);
        }
        Assert.Fail("Canary proxy did not become ready: " + await Docker("logs", container));
    }

    private static async Task<string> Docker(params string[] arguments)
    {
        var result = await RunDocker(arguments);
        Assert.True(result.Code == 0, $"docker {string.Join(' ', arguments)} failed: {result.Output}");
        return result.Output;
    }

    private static async Task<(int Code, string Output)> RunDocker(params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        return (process.ExitCode, await output + await error);
    }
}
