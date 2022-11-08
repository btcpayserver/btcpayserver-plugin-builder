#nullable disable
using System.Reflection;
using Microsoft.Build.Framework;

namespace PluginBuilder.Targets;
public class Packer : ITask
{
    public IBuildEngine BuildEngine { get; set; }
    public ITaskHost HostObject { get; set; }

    public bool Execute()
    {
        return true;
    }

    public string PluginDll { get; set; }
    [Output]
    public string Output { get; set; }
}
