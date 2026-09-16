using System.Text.Json;
using PluginBuilder.BuildBroker;
using PluginBuilder.Util;
using Xunit;

using PluginBuilder.BuildBroker.HostedServices;
using PluginBuilder.BuildBroker.Services;
using PluginBuilder.Builds.Services;

namespace PluginBuilder.Tests;

public class BuildDependencyBoundaryTests
{
    [Fact]
    public void WebAndBrokerReferenceOnlyTheSharedBuildContractNotEachOther()
    {
        var web = typeof(Program).Assembly;
        var broker = typeof(BuildBrokerApplication).Assembly;
        var shared = typeof(IBuildSandbox).Assembly;
        Assert.Contains(web.GetReferencedAssemblies(), name => name.Name == shared.GetName().Name);
        Assert.Contains(broker.GetReferencedAssemblies(), name => name.Name == shared.GetName().Name);
        Assert.DoesNotContain(web.GetReferencedAssemblies(), name => name.Name == broker.GetName().Name);
        Assert.DoesNotContain(broker.GetReferencedAssemblies(), name => name.Name == web.GetName().Name);
    }

    [Theory]
    [InlineData(typeof(DockerBuildSandbox))]
    [InlineData(typeof(DockerStartupHostedService))]
    [InlineData(typeof(BuildScratchCleaner))]
    [InlineData(typeof(ProcessRunner))]
    public void PrivilegedImplementationExistsOnlyInTheBrokerAssembly(Type implementation)
    {
        Assert.Same(typeof(BuildBrokerApplication).Assembly, implementation.Assembly);
        Assert.Null(typeof(Program).Assembly.GetType(implementation.FullName!));
        Assert.Null(typeof(IBuildSandbox).Assembly.GetType(implementation.FullName!));
    }

    [Fact]
    public void BrokerRuntimeDependencyGraphDoesNotIncludeThePublicApplicationStack()
    {
        var path = Path.ChangeExtension(typeof(BuildBrokerApplication).Assembly.Location, ".deps.json");
        using var deps = JsonDocument.Parse(File.ReadAllText(path));
        var libraries = deps.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(property => property.Name.Split('/')[0]).ToArray();
        Assert.Contains(typeof(IBuildSandbox).Assembly.GetName().Name, libraries);
        Assert.DoesNotContain(typeof(Program).Assembly.GetName().Name, libraries);
    }
}
