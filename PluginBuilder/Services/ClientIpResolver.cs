using System.Diagnostics.CodeAnalysis;
using System.Net;
using PluginBuilder.Util.Extensions;

namespace PluginBuilder.Services;

public sealed class ClientIpResolver
{
    private const string TrustedProxiesKey = "CLIENT_IP_TRUSTED_PROXIES";
    private static readonly object SourceAddressKey = new();

    private readonly IPNetwork[] _trustedProxies;

    public ClientIpResolver(IConfiguration configuration)
    {
        var entries = (configuration[TrustedProxiesKey] ?? "").Split(
            [',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _trustedProxies = entries.Select(ParseProxy).ToArray();
    }

    // Call before forwarded-header middleware mutates Connection.RemoteIpAddress.
    public void Capture(HttpContext http)
    {
        if (http.Items.ContainsKey(SourceAddressKey))
            return;

        var source = Canonicalize(http.Connection.RemoteIpAddress);
        if (source is not null && _trustedProxies.Any(network => network.Contains(source)))
        {
            // nginx appends its actual connecting peer. Older claims belong to the caller.
            var forwarded = http.Request.Headers["X-Forwarded-For"].ToString();
            source = TryAddress(forwarded[(forwarded.LastIndexOf(',') + 1)..], out var peer) ? peer : null;
        }

        http.Items[SourceAddressKey] = source;
    }

    public static IPAddress? Get(HttpContext http) =>
        http.Items.TryGetValue(SourceAddressKey, out var address) ? address as IPAddress : null;

    private static IPNetwork ParseProxy(string entry)
    {
        if (TryAddress(entry, out var address))
            return new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);

        if (!entry.Contains('%') && IPNetwork.TryParse(entry, out var network))
        {
            if (!network.BaseAddress.IsIPv4MappedToIPv6)
                return network;
            if (network.PrefixLength >= 96)
                return new IPNetwork(network.BaseAddress.MapToIPv4(), network.PrefixLength - 96);
        }

        throw new ConfigurationException(TrustedProxiesKey,
            "Must contain IP addresses or CIDR networks separated by commas, semicolons or whitespace");
    }

    private static bool TryAddress(string? value, [NotNullWhen(true)] out IPAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(value) || value.Contains('%') || value.Contains(',') ||
            !IPAddress.TryParse(value.Trim(), out var parsed))
            return false;
        address = Canonicalize(parsed);
        return true;
    }

    [return: NotNullIfNotNull(nameof(address))]
    private static IPAddress? Canonicalize(IPAddress? address) =>
        address is null ? null : address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : new IPAddress(address.GetAddressBytes());
}
