using System.Security.Cryptography;
using System.Text;
using PluginBuilder.Builds.BuildBroker;

namespace PluginBuilder.BuildBroker;

public sealed record BuildBrokerSettings(string TokenFile, TimeSpan LeaseLifetime);

public sealed class BrokerAuthentication
{
    private readonly byte[] _expected;
    public BrokerAuthentication(BuildBrokerSettings settings)
    {
        if (!BuildBrokerProtocol.TryReadTokenFile(settings.TokenFile, out var token))
            throw new InvalidOperationException("Broker token file must be an absolute path to a 256-bit hexadecimal secret.");
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
