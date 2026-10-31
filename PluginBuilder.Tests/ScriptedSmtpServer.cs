using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PluginBuilder.Tests;

internal sealed class ScriptedSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _firstMessageReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _server;

    public ScriptedSmtpServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _server = Run();
    }

    public int Port { get; }
    public string? RejectRecipient { get; set; }
    public string? DisconnectAfterDataRecipient { get; set; }
    public bool RejectQuit { get; set; }
    public TimeSpan DeliveryDelay { get; set; }
    public ConcurrentQueue<string> AttemptedRecipients { get; } = new();
    public ConcurrentQueue<string> AcceptedRecipients { get; } = new();
    public Task FirstMessageReceived => _firstMessageReceived.Task;

    private async Task Run()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                try
                {
                    await Handle(client);
                }
                catch (IOException) { } // The client may close a timed-out SMTP connection.
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }

    private async Task Handle(TcpClient client)
    {
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("220 localhost test SMTP");
        string? recipient = null;
        while (await reader.ReadLineAsync(_stop.Token) is { } command)
        {
            if (command.StartsWith("EHLO ") || command.StartsWith("HELO "))
                await writer.WriteLineAsync("250 localhost");
            else if (command.StartsWith("MAIL FROM:"))
                await writer.WriteLineAsync("250 Sender accepted");
            else if (command.StartsWith("RCPT TO:"))
            {
                recipient = command[(command.IndexOf('<') + 1)..command.IndexOf('>')];
                AttemptedRecipients.Enqueue(recipient);
                await writer.WriteLineAsync(recipient == RejectRecipient
                    ? "550 Recipient rejected by test server"
                    : "250 Recipient accepted");
            }
            else if (command == "DATA")
            {
                await writer.WriteLineAsync("354 End with a dot");
                string? line;
                do
                {
                    line = await reader.ReadLineAsync(_stop.Token);
                    if (line is null)
                        return;
                } while (line != ".");
                _firstMessageReceived.TrySetResult();
                if (DeliveryDelay > TimeSpan.Zero)
                    await Task.Delay(DeliveryDelay, _stop.Token);
                AcceptedRecipients.Enqueue(recipient!);
                if (recipient == DisconnectAfterDataRecipient)
                    return;
                await writer.WriteLineAsync("250 Message accepted");
            }
            else if (command == "RSET")
            {
                recipient = null;
                await writer.WriteLineAsync("250 Reset");
            }
            else if (command == "QUIT")
            {
                await writer.WriteLineAsync(RejectQuit ? "421 Closing connection" : "221 Bye");
                return;
            }
            else
                throw new InvalidOperationException($"Unexpected SMTP command: {command}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _server;
        }
        finally
        {
            _stop.Dispose();
        }
    }
}
