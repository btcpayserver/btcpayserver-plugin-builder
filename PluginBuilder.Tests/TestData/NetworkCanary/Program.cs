using System.Net;
using System.Net.Sockets;
using System.Text;

// A bounded, benign fixture. Every address comes from the test's own Docker
// networks; DNS answers point at loopback and never query another resolver.
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            switch (args[0])
            {
                case "serve":
                    await Task.WhenAll(ServeDns(), ServeTcp(443), ServeTcp(5432), ServeTcp(8080));
                    break;
                case "control":
                    foreach (var port in new[] { 443, 5432, 8080 })
                        Require(await CanConnect(args[1], port), $"Fixture listener {port} is unavailable");
                    Require(await QueryDns(args[1], "control.canary.test"), "Fixture DNS is unavailable");
                    Console.WriteLine("CONTROL=PASS");
                    break;
                case "probe":
                    await Probe(args[1], args[2]);
                    break;
                default:
                    throw new ArgumentException("Unknown fixture mode");
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task Probe(string proxy, string fixture)
    {
        // Numeric metadata/public addresses are submitted only to Squid, never
        // contacted directly. The fixture egress network itself has no egress.
        foreach (var target in new[]
                 {
                     "denied.canary.test:443", "169.254.169.254:443", "203.0.113.42:443",
                     "127.0.0.1:443", $"{fixture}:443", "github.com:80", "github.com:443",
                     "gitlab.com:443", "api.nuget.org:443"
                 })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Parse(proxy), 3128, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n\r\n"),
                timeout.Token);
            using var reader = new StreamReader(stream);
            var status = await reader.ReadLineAsync(timeout.Token);
            Require(status is not null && status.StartsWith("HTTP/1.1 403 ", StringComparison.Ordinal),
                $"Expected proxy denial for {target}, got {status}");
        }

        foreach (var port in new[] { 443, 5432, 8080 })
            Require(!await CanConnect(fixture, port), $"Worker reached isolated fixture port {port} directly");
        Require(!await QueryDns(fixture, "direct.canary.test"), "Worker reached DNS directly");
        Require(!await QueryDns("127.0.0.11", "embedded.canary.test"), "Docker embedded DNS resolved a forbidden name");
        var routes = File.ReadAllLines("/proc/net/route").Skip(1)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Require(!routes.Any(fields => fields.Length > 2 && fields[0] != "lo" && fields[1] == "00000000"),
            "Worker has an external default route");
        Require(!File.Exists("/var/run/docker.sock"), "Worker has a Docker socket");
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("CONNECTION_STRING", StringComparison.OrdinalIgnoreCase))
                Require(string.IsNullOrEmpty((string?)entry.Value), "Worker has a credential environment variable");
        }
        Console.WriteLine("NETWORK_CANARY=PASS");
    }

    private static async Task<bool> CanConnect(string address, int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Parse(address), port, timeout.Token);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> QueryDns(string address, string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var client = new UdpClient(AddressFamily.InterNetwork);
        var query = new List<byte> { 0x43, 0x21, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            query.Add((byte)label.Length);
            query.AddRange(Encoding.ASCII.GetBytes(label));
        }
        query.AddRange([0, 0, 1, 0, 1]);
        try
        {
            await client.SendAsync(query.ToArray(), new IPEndPoint(IPAddress.Parse(address), 53), timeout.Token);
            var response = (await client.ReceiveAsync(timeout.Token)).Buffer;
            return response.Length >= 12 && response[0] == 0x43 && response[1] == 0x21 && response[7] > 0;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task ServeDns()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Any, 53));
        Console.WriteLine("READY=DNS");
        while (true)
        {
            var request = await server.ReceiveAsync();
            var bytes = request.Buffer;
            if (bytes.Length < 17 || bytes.Length > 512)
                continue;
            var labels = new List<string>();
            var offset = 12;
            while (offset < bytes.Length && bytes[offset] is > 0 and <= 63)
            {
                var length = bytes[offset++];
                if (offset + length >= bytes.Length)
                    break;
                labels.Add(Encoding.ASCII.GetString(bytes, offset, length));
                offset += length;
            }
            if (offset + 5 > bytes.Length || bytes[offset] != 0)
                continue;
            Console.WriteLine("DNS=" + string.Join('.', labels));
            var answer = bytes[offset + 1] == 0 && bytes[offset + 2] == 1;
            var response = bytes[..(offset + 5)].ToList();
            response[2] = 0x81;
            response[3] = 0x80;
            response[6] = 0;
            response[7] = answer ? (byte)1 : (byte)0;
            response[8] = response[9] = response[10] = response[11] = 0;
            if (answer)
                response.AddRange([0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 1, 0, 4, 127, 0, 0, 1]);
            await server.SendAsync(response.ToArray(), request.RemoteEndPoint);
        }
    }

    private static async Task ServeTcp(int port)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"READY={port}");
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync();
            Console.WriteLine($"CONNECTION={port}");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
