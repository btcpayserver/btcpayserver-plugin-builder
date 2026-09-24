using Xunit;

using PluginBuilder.BuildBroker;

namespace PluginBuilder.Tests;

public class ProcessRunnerTests
{
    [UnixTheory]
    [InlineData(0, true)]
    [InlineData(7, true)]
    [InlineData(0, false)]
    public async Task ExitDrainsBothStreamsIncludingFinalUnterminatedLines(int exitCode, bool capture)
    {
        var output = new OutputCapture();
        var error = new OutputCapture();
        var runner = new ProcessRunner();
        var result = await runner.RunAsync(new ProcessSpec
        {
            Executable = "sh",
            Arguments = ["-c", "i=0; while [ $i -lt 2000 ]; do echo out-$i; echo err-$i >&2; i=$((i+1)); done; printf final-out; printf final-err >&2; exit \"$1\"", "runner-test", exitCode.ToString()],
            OutputCapture = capture ? output : null,
            ErrorCapture = capture ? error : null
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(exitCode, result);
        if (capture)
        {
            Assert.Equal(Enumerable.Range(0, 2000).Select(i => $"out-{i}").Append("final-out"), output.Lines);
            Assert.Equal(Enumerable.Range(0, 2000).Select(i => $"err-{i}").Append("final-err"), error.Lines);
        }
        else
        {
            Assert.Empty(output.Lines);
            Assert.Empty(error.Lines);
        }
    }

    [UnixFact]
    public async Task OverlongLinesAreTruncatedWithoutBufferingTheWholeLine()
    {
        var output = new OutputCapture();
        var error = new OutputCapture();
        var result = await new ProcessRunner().RunAsync(new ProcessSpec
        {
            Executable = "sh",
            // 50 MB without a newline on each stream, then a normal line.
            Arguments = ["-c", "head -c 50000000 /dev/zero | tr '\\0' x; printf '\\nnext\\n'; head -c 50000000 /dev/zero | tr '\\0' y >&2; printf '\\nnext\\n' >&2"],
            OutputCapture = output,
            ErrorCapture = error
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(0, result);
        var limit = PluginBuilder.Builds.BuildBroker.BuildBrokerProtocol.MaximumLogLineCharacters;
        Assert.Equal([new string('x', limit), "next"], output.Lines);
        Assert.Equal([new string('y', limit), "next"], error.Lines);
    }

    [UnixFact]
    public async Task CancellationTerminatesRunningProcess()
    {
        var runner = new ProcessRunner();
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
        var runner = new ProcessRunner();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(new ProcessSpec
        {
            Executable = Path.Combine(Path.GetTempPath(), $"missing-process-{Guid.NewGuid():N}")
        }, cancellation.Token));
    }
}
