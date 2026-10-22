using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PluginBuilder.Services;

/// <summary>One request from the front proxy's trimmed access log.</summary>
public sealed record AccessLogEntry(DateTimeOffset LoggedAt, IPAddress Ip, string Host, string Method, string Path, int Status, long Bytes,
    string UserAgent);

/// <summary>
/// Parses the syslog datagrams nginx sends for <c>access_log syslog:server=...,tag=pbaccess,nohostname pbaccess;</c>:
/// <c>&lt;PRI&gt;Mmm dd hh:mm:ss pbaccess: {json}</c>, the JSON written by the <c>pbaccess</c> log_format with
/// <c>escape=json</c>. Anything else is refused, so a stray datagram cannot write a row.
/// </summary>
public static class AccessLogLine
{
    public const string Tag = "pbaccess";
    public const int MaxHost = 255, MaxMethod = 16, MaxPath = 2048, MaxUserAgent = 512;
    private static readonly byte[] Marker = Encoding.ASCII.GetBytes(" " + Tag + ": ");

    public static bool TryParse(ReadOnlySpan<byte> datagram, out AccessLogEntry? entry)
    {
        entry = null;
        if (datagram.IsEmpty || datagram[0] != (byte)'<')
            return false;
        var start = datagram.IndexOf(Marker);
        if (start < 0)
            return false;
        try
        {
            // nginx copies request bytes as they arrived, so a client can send invalid UTF-8 (say in its user agent). Decode
            // it leniently, as U+FFFD, rather than refuse the line: a refused line would hide that client's requests.
            using var json = JsonDocument.Parse(Encoding.UTF8.GetString(datagram[(start + Marker.Length)..]));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !DateTimeOffset.TryParse(Text(root, "time"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var loggedAt) ||
                !IPAddress.TryParse(Text(root, "ip"), out var ip) ||
                !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Number ||
                !status.TryGetInt32(out var code) || code is < 100 or > 599 ||
                !root.TryGetProperty("bytes", out var bytes) || bytes.ValueKind != JsonValueKind.Number ||
                !bytes.TryGetInt64(out var sent) || sent < 0)
                return false;
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0)
                ip = new IPAddress(ip.GetAddressBytes());
            entry = new AccessLogEntry(loggedAt.ToUniversalTime(), ip, Safe(Text(root, "host"), MaxHost), Safe(Text(root, "method"), MaxMethod),
                Safe(Text(root, "path"), MaxPath), code, sent, Safe(Text(root, "ua"), MaxUserAgent));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Parses an address ("203.0.113.7") or a block ("2001:db8::/64") into the form Postgres reads as inet.</summary>
    public static bool TryParseNetwork(string value, out string network)
    {
        network = "";
        var slash = value.IndexOf('/');
        if (!IPAddress.TryParse(slash < 0 ? value : value[..slash], out var address) ||
            address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            return false;
        if (address.IsIPv4MappedToIPv6 && slash < 0)
            address = address.MapToIPv4();
        var max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = max;
        if (slash >= 0 && (!int.TryParse(value[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix > max))
            return false;
        network = $"{address}/{prefix}";
        return true;
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // nginx escapes control characters; decoded they would reach the database (which refuses NUL) and log viewers.
    private static string Safe(string? value, int limit)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var length = Math.Min(value.Length, limit);
        if (length < value.Length && char.IsHighSurrogate(value[length - 1]))
            length--; // never keep half of a surrogate pair, which the database's UTF-8 encoder refuses
        var chars = value.Take(length).Select(c => char.IsControl(c) ||
            char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                ? ' ' : c).ToArray();
        return new string(chars);
    }
}
