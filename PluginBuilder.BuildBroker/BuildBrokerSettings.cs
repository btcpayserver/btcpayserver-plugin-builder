using System.Security.Cryptography;
using System.Text;

namespace PluginBuilder.BuildBroker;

public sealed record BuildBrokerSettings(string TokenFile, TimeSpan LeaseLifetime);

public sealed class BrokerAuthentication
{
    private readonly byte[] _expected;
    public BrokerAuthentication(BuildBrokerSettings settings)
    {
        if (!Path.IsPathFullyQualified(settings.TokenFile))
            throw new InvalidOperationException("Broker token file must be an absolute path.");
        var file = new FileInfo(settings.TokenFile);
        if (!file.Exists || file.LinkTarget is not null || file.Length is < 64 or > 66)
            throw new InvalidOperationException("Broker token file must contain a 256-bit hexadecimal secret.");
        using var stream = new FileStream(settings.TokenFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> bytes = stackalloc byte[67];
        var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        var token = Encoding.ASCII.GetString(bytes[..count]).TrimEnd('\r', '\n');
        if (token.Length != 64 || token.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new InvalidOperationException("Broker token file must contain a 256-bit hexadecimal secret.");
        _expected = Encoding.ASCII.GetBytes(token);
    }

    public bool Authorize(HttpRequest request)
    {
        if (request.Headers.Authorization.Count != 1) return false;
        var header = request.Headers.Authorization.ToString();
        if (header.Length != 71 || !header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(header[7..]), _expected);
    }

    public string HeaderForHealthcheck() => "Bearer " + Encoding.ASCII.GetString(_expected);
}
