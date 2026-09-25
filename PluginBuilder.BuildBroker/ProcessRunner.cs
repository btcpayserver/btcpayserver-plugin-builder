using System.Diagnostics;
using System.Text;
using PluginBuilder.Builds;
using PluginBuilder.Builds.BuildBroker;

namespace PluginBuilder.BuildBroker;

public class OutputCapture : IOutputCapture
{
    private readonly List<string> _lines = new();

    public IEnumerable<string> Lines
    {
        get => _lines;
    }

    public void AddLine(string line)
    {
        _lines.Add(line);
    }

    public override string ToString()
    {
        return string.Join(Environment.NewLine, _lines);
    }
}

public class ProcessSpec
{
    public string? Executable { get; set; }
    public IReadOnlyList<string>? Arguments { get; set; }
    public IOutputCapture? OutputCapture { get; set; }
    public IOutputCapture? ErrorCapture { get; set; }
}

public class ProcessRunner
{
    public async Task<int> RunAsync(ProcessSpec processSpec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processSpec, nameof(processSpec));
        cancellationToken.ThrowIfCancellationRequested();

        int exitCode;

        using (var process = CreateProcess(processSpec))
        {
            process.Start();
            using var cancellationRegistration = cancellationToken.Register(() => TryKill(process));
            try
            {
                // Always drain both streams; uncaptured output must not inherit the host console.
                var output = ReadLinesAsync(process.StandardOutput, processSpec.OutputCapture);
                var error = ReadLinesAsync(process.StandardError, processSpec.ErrorCapture);

                // Cancellation kills the process; wait without a token to drain both streams.
                await process.WaitForExitAsync();
                await Task.WhenAll(output, error);
                cancellationToken.ThrowIfCancellationRequested();
                exitCode = process.ExitCode;
            }
            finally { TryKill(process); }
        }

        return exitCode;
    }

    private Process CreateProcess(ProcessSpec processSpec)
    {
        Process process = new()
        {
            StartInfo =
            {
                FileName = processSpec.Executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        if (processSpec.Arguments is not null)
            for (var i = 0; i < processSpec.Arguments.Count; i++)
                process.StartInfo.ArgumentList.Add(processSpec.Arguments[i]);

        return process;
    }

    // Untrusted build output may run for gigabytes without a newline. Keep at most one
    // bounded line in memory and drop the rest of an overlong line until its end.
    private static async Task ReadLinesAsync(StreamReader reader, IOutputCapture? capture)
    {
        var buffer = new char[8192];
        var line = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var character = buffer[i];
                if (character is '\n' or '\r')
                    Complete();
                else if (line.Length < BuildBrokerProtocol.MaximumLogLineCharacters)
                    line.Append(character);
            }
        }
        Complete();

        void Complete()
        {
            if (line.Length > 0)
                capture?.AddLine(line.ToString());
            line.Clear();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception) { }
    }
}
