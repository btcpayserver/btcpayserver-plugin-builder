using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Xunit.Abstractions;

namespace PluginBuilder.Tests
{
    public class UnitTestBase
    {
        public UnitTestBase(ITestOutputHelper log)
        {
            Log = new XUnitLogger("Tests", log);
        }

        public XUnitLogger Log { get; }


        public async Task<ServerTester> Start([CallerMemberName] string? caller = null)
        {
            ServerTester tester = Create(caller);
            await tester.Start();
            return tester;
        }

        public ServerTester Create([CallerMemberName] string? caller = null)
        {
            return new ServerTester(caller ?? "Default", Log);
        }
    }
}
