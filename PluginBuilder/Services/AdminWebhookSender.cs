using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace PluginBuilder.Services;

public sealed class AdminWebhookSender : IDisposable
{
    private readonly HttpClient _client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        ConnectCallback = ConnectPublicEndpoint
    }) { Timeout = TimeSpan.FromSeconds(15) };

    public static bool IsValidDestination(string destination) =>
        Uri.TryCreate(destination, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) &&
        (!IPAddress.TryParse(uri.DnsSafeHost, out var ip) || IsPublicAddress(ip));

    // Resolve and validate at connection time, then connect to the checked address.
    // This also prevents DNS rebinding and redirects into local/cloud metadata services.
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224 &&
                   !(b[0] == 100 && b[1] >= 64 && b[1] <= 127) &&
                   !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) &&
                   !(b[0] == 192 && (b[1] == 168 || b[1] == 0 && (b[2] == 0 || b[2] == 2) || b[1] == 88 && b[2] == 99)) &&
                   !(b[0] == 168 && b[1] == 63 && b[2] == 129 && b[3] == 16) &&
                   !(b[0] == 198 && (b[1] == 18 || b[1] == 19 || b[1] == 51 && b[2] == 100)) &&
                   !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        // Only global unicast; exclude special-purpose, documentation and 6to4.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xe0) == 0x20 &&
               !(b[0] == 0x20 && b[1] == 0x01 && (b[2] < 2 || b[2] == 0x0d && b[3] == 0xb8)) &&
               !(b[0] == 0x20 && b[1] == 0x02) && !(b[0] == 0x3f && b[1] == 0xff && b[2] < 0x10);
    }

    private static async ValueTask<Stream> ConnectPublicEndpoint(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(ip => !IsPublicAddress(ip)))
            throw new HttpRequestException("Webhook destination must resolve exclusively to public addresses.");
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                if (address.Equals(addresses[^1]))
                    throw;
            }
        }
        throw new HttpRequestException("Unable to connect to webhook destination.");
    }

    public static string Sign(string secret, string eventId, string timestamp, string body) =>
        "v1," + Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(secret),
            Encoding.UTF8.GetBytes($"{eventId}.{timestamp}.{body}")));

    public async Task Send(string destination, string secret, string eventId, string body, CancellationToken cancellationToken)
    {
        if (!IsValidDestination(destination))
            throw new HttpRequestException("Invalid webhook destination.");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, destination);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Add("webhook-id", eventId);
        request.Headers.Add("webhook-timestamp", timestamp);
        request.Headers.Add("webhook-signature", Sign(secret, eventId, timestamp, body));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Webhook returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
    }

    public void Dispose() => _client.Dispose();
}
