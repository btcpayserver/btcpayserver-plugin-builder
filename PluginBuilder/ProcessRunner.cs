using System.Diagnostics;

namespace PluginBuilder;

public interface IOutputCapture
{
    void AddLine(string line);
}

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
    public string? WorkingDirectory { get; set; }
    public ProcessSpecEnvironmentVariables EnvironmentVariables { get; } = new();

    public IReadOnlyList<string>? Arguments { get; set; }
    public string? EscapedArguments { get; set; }
    public IOutputCapture? OutputCapture { get; set; }
    public IOutputCapture? ErrorCapture { get; set; }

    public DataReceivedEventHandler? OnOutput { get; set; }
    public DataReceivedEventHandler? OnError { get; set; }
    public string? Input { get; set; }

    public sealed class ProcessSpecEnvironmentVariables : Dictionary<string, string>
    {
        public List<string> DotNetStartupHooks { get; } = new();
        public List<string> AspNetCoreHostingStartupAssemblies { get; } = new();
    }
}

public class ProcessRunner
{
    private static readonly Func<string, string?> _getEnvironmentVariable = static key => Environment.GetEnvironmentVariable(key);

    public ProcessRunner(ILogger<ProcessRunner> logger)
    {
        Logger = logger;
    }

    private ILogger<ProcessRunner> Logger { get; }

    // May not be necessary in the future. See https://github.com/dotnet/corefx/issues/12039
    public async Task<int> RunAsync(ProcessSpec processSpec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processSpec, nameof(processSpec));

        int exitCode;

        Stopwatch stopwatch = new();

        using (var process = CreateProcess(processSpec))
        using (ProcessState processState = new(process))
        {
            cancellationToken.Register(() => processState.TryKill());

            var readOutput = false;
            var readError = false;
            if (processSpec.OutputCapture is not null)
            {
                readOutput = true;
                process.OutputDataReceived += (_, a) =>
                {
                    if (!string.IsNullOrEmpty(a.Data))
                        processSpec.OutputCapture.AddLine(a.Data);
                };
            }

            if (processSpec.OnOutput != null)
            {
                readOutput = true;
                process.OutputDataReceived += processSpec.OnOutput;
            }

            if (processSpec.ErrorCapture is not null)
            {
                readError = true;
                process.ErrorDataReceived += (_, a) =>
                {
                    if (!string.IsNullOrEmpty(a.Data))
                        processSpec.ErrorCapture.AddLine(a.Data);
                };
            }

            if (processSpec.OnError is not null)
            {
                readError = true;
                process.ErrorDataReceived += processSpec.OnError;
            }

            if (Logger.IsEnabled(LogLevel.Trace))
            {
                readOutput = true;
                readError = true;
                process.OutputDataReceived += (s, a) =>
                {
                    // a.Data.EndsWith("\u001b[K")
                    Logger.LogInformation(a.Data);
                };
                process.ErrorDataReceived += (s, a) =>
                {
                    Logger.LogWarning(a.Data);
                };
            }


            stopwatch.Start();
            process.Start();

            if (readOutput)
                process.BeginOutputReadLine();
            if (readError)
                process.BeginErrorReadLine();

            if (processSpec.Input is not null)
            {
                await process.StandardInput.WriteLineAsync(processSpec.Input);
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();
            }

            await processState.Task;

            exitCode = process.ExitCode;
            stopwatch.Stop();
        }

        return exitCode;
    }

    private Process CreateProcess(ProcessSpec processSpec)
    {
        Process process = new()
        {
            EnableRaisingEvents = true,
            StartInfo =
            {
                FileName = processSpec.Executable,
                UseShellExecute = false,
                WorkingDirectory = processSpec.WorkingDirectory,
                RedirectStandardOutput = processSpec.OutputCapture is not null || processSpec.OnOutput is not null || Logger.IsEnabled(LogLevel.Trace),
                RedirectStandardError = processSpec.ErrorCapture is not null || processSpec.OnError is not null || Logger.IsEnabled(LogLevel.Trace),
                RedirectStandardInput = processSpec.Input is not null
            }
        };

        if (processSpec.EscapedArguments is not null)
            process.StartInfo.Arguments = processSpec.EscapedArguments;
        else if (processSpec.Arguments is not null)
            for (var i = 0; i < processSpec.Arguments.Count; i++)
                process.StartInfo.ArgumentList.Add(processSpec.Arguments[i]);

        foreach (var env in processSpec.EnvironmentVariables)
            process.StartInfo.Environment.Add(env.Key, env.Value);

        SetEnvironmentVariable(process.StartInfo, "DOTNET_STARTUP_HOOKS", processSpec.EnvironmentVariables.DotNetStartupHooks, Path.PathSeparator,
            _getEnvironmentVariable);
        SetEnvironmentVariable(process.StartInfo, "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", processSpec.EnvironmentVariables.AspNetCoreHostingStartupAssemblies,
            ';', _getEnvironmentVariable);

        return process;
    }

    internal static void SetEnvironmentVariable(ProcessStartInfo processStartInfo, string envVarName, List<string> envVarValues, char separator,
        Func<string, string?> getEnvironmentVariable)
    {
        if (envVarValues is { Count: 0 })
            return;

        var existing = getEnvironmentVariable(envVarName);
        if (processStartInfo.Environment.TryGetValue(envVarName, out var value))
            existing = CombineEnvironmentVariable(existing, value, separator);

        string result;
        if (!string.IsNullOrEmpty(existing))
            result = existing + separator + string.Join(separator, envVarValues);
        else
            result = string.Join(separator, envVarValues);

        processStartInfo.EnvironmentVariables[envVarName] = result;

        static string? CombineEnvironmentVariable(string? a, string? b, char separator)
        {
            if (!string.IsNullOrEmpty(a))
                return !string.IsNullOrEmpty(b) ? a + separator + b : a;

            return b;
        }
    }

    private class ProcessState : IDisposable
    {
        private readonly Process _process;
        private readonly TaskCompletionSource _tcs = new();
        private volatile bool _disposed;

        public ProcessState(Process process)
        {
            _process = process;
            _process.Exited += OnExited;
            Task = _tcs.Task.ContinueWith(_ =>
            {
                try
                {
                    // We need to use two WaitForExit calls to ensure that all of the output/events are processed. Previously
                    // this code used Process.Exited, which could result in us missing some output due to the ordering of
                    // events.
                    //
                    // See the remarks here: https://docs.microsoft.com/en-us/dotnet/api/system.diagnostics.process.waitforexit#System_Diagnostics_Process_WaitForExit_System_Int32_
                    if (!_process.WaitForExit(int.MaxValue))
                        throw new TimeoutException();

                    _process.WaitForExit();
                }
                catch (InvalidOperationException)
                {
                    // suppress if this throws if no process is associated with this object anymore.
                }
            });
        }

        public Task Task { get; }

        public void Dispose()
        {
            if (!_disposed)
            {
                TryKill();
                _disposed = true;
                _process.Exited -= OnExited;
                _process.Dispose();
            }
        }

        public void TryKill()
        {
            if (_disposed)
                return;

            try
            {
                if (_process is not null && !_process.HasExited)
                    _process.Kill(true);
            }
            catch (Exception)
            {
            }
        }

        private void OnExited(object? sender, EventArgs args)
        {
            _tcs.TrySetResult();
        }
    }
}
