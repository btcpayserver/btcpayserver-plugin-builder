using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

using PluginBuilder.BuildBroker;

namespace PluginBuilder.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public async Task CancellationTerminatesRunningProcess()
    {
        if (OperatingSystem.IsWindows())
            return;

        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await runner.RunAsync(new ProcessSpec
            {
                Executable = "sleep",
                Arguments = ["30"]
            }, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task PreCancelledTokenDoesNotStartProcess()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(new ProcessSpec
        {
            Executable = Path.Combine(Path.GetTempPath(), $"missing-process-{Guid.NewGuid():N}")
        }, cancellation.Token));
    }
}
