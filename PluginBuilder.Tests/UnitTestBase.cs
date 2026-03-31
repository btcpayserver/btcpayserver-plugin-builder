using System.Runtime.CompilerServices;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

public class UnitTestBase
{
    public UnitTestBase(ITestOutputHelper log)
    {
        Log = new XUnitLogger("Tests", log);
    }

    public XUnitLogger Log { get; }


    public async Task<ServerTester> Start([CallerMemberName] string? caller = null)
    {
        var tester = Create(caller);
        await tester.Start();
        return tester;
    }

    public ServerTester Create([CallerMemberName] string? caller = null)
    {
        return new ServerTester(caller ?? "Default", Log);
    }

    public ScriptMigrationTester CreateMigrationTester([CallerMemberName] string? caller = null)
    {
        return new ScriptMigrationTester(caller ?? "Default", Log);
    }
}
