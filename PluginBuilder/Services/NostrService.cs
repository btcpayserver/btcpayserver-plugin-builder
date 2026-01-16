using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PluginBuilder.Controllers.Logic;
using PluginBuilder.DataModels;
using JsonException = System.Text.Json.JsonException;
using SHA256 = System.Security.Cryptography.SHA256;

namespace PluginBuilder.Services;

public class NostrService(IMemoryCache cache, AdminSettingsCache adminSettingsCache)
{
    private const string PrefixNpub = "npub";
    private const string PrefixNote = "note";
    private const string PrefixNevent = "nevent";
    private const string PrefixNprofile = "nprofile";

    private static readonly Regex _nip19Regex = new("(?:nevent1|note1)[02-9ac-hj-np-z]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<string> DefaultRelays { get; } = new[]
    {
        "wss://relay.damus.io",
        "wss://relay.primal.net",
        "wss://nos.lol",
        "wss://nostr.wine"
    };

    private static string ChallengeCacheKey(string userId)
    {
        return $"nostr:active:{userId}";
    }

    public string GetOrCreateActiveChallenge(string userId)
    {
        var now = DateTimeOffset.UtcNow;
        var key = ChallengeCacheKey(userId);

        if (cache.TryGetValue<NostrChallenge>(key, out var challenge) && challenge!.ExpiresAt > now)
            return challenge.Token;

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        var expiresAt = now.Add(TimeSpan.FromMinutes(30));
        cache.Set(key, new NostrChallenge(token, userId, expiresAt), new MemoryCacheEntryOptions { AbsoluteExpiration = expiresAt });
        return token;
    }

    public bool IsValidChallenge(string userId, string providedToken)
    {
        if (!cache.TryGetValue<NostrChallenge>(ChallengeCacheKey(userId), out var challenge))
            return false;

        return string.Equals(challenge?.Token, providedToken, StringComparison.Ordinal) && challenge?.ExpiresAt > DateTimeOffset.UtcNow;
    }

    public static bool HasTag(NostrEvent ev, string key, string value)
    {
        return ev.Tags.Any(tag => tag.Length >= 2 && tag[0] == key && tag[1] == value);
    }

    public static bool VerifyEvent(NostrEvent ev)
    {
        var serializedEvent = JsonConvert.SerializeObject(
            new object[] { 0, ev.Pubkey, ev.CreatedAt, ev.Kind, ev.Tags, ev.Content },
            new JsonSerializerSettings { Formatting = Formatting.None }
        );
        var id = SHA256.HashData(Encoding.UTF8.GetBytes(serializedEvent));
        var idHex = Convert.ToHexString(id).ToLowerInvariant();
        return idHex.Equals(ev.Id, StringComparison.OrdinalIgnoreCase) && VerifyBip340(ev.Pubkey, ev.Sig, id);
    }

    private static bool VerifyBip340(string pubkeyHex, string sigHex, byte[] msg32)
    {
        try
        {
            var pubKey = Convert.FromHexString(pubkeyHex);
            var signature = Convert.FromHexString(sigHex);
            if (!ECXOnlyPubKey.TryCreate(pubKey, Context.Instance, out var xOnly))
                return false;
            return SecpSchnorrSignature.TryCreate(signature, out var s) && xOnly.SigVerifyBIP340(s, msg32);
        }
        catch { return false; }
    }

    public static string HexPubToNpub(string hex32)
    {
        var raw = Convert.FromHexString(hex32);
        if (raw.Length != 32)
            throw new ArgumentException("Nostr pubkey must be 32 bytes (x-only).", nameof(hex32));
        var data5 = ConvertBits(raw, 8, 5, true);
        return Encoders.Bech32(PrefixNpub).EncodeData(data5, Bech32EncodingType.BECH32);
    }

    public string NpubToHexPub(string npub)
    {
        if (string.IsNullOrWhiteSpace(npub))
            throw new ArgumentNullException(nameof(npub));
        var data5 = Encoders.Bech32(PrefixNpub).DecodeDataRaw(npub.Trim(), out _);
        var raw = ConvertBits(data5, 5, 8, false);
        if (raw.Length != 32)
            throw new FormatException("Invalid npub payload length");
        return Convert.ToHexString(raw).ToLowerInvariant();
    }

    public bool TryGetPubKeyHex(string? identifier, [NotNullWhen(true)] out string? pubKeyHex)
    {
        pubKeyHex = null;
        if (string.IsNullOrWhiteSpace(identifier))
            return false;
        identifier = identifier.Trim();

        if (IsHex64(identifier))
        {
            pubKeyHex = identifier.ToLowerInvariant();
            return true;
        }

        if (!identifier.StartsWith(PrefixNpub, StringComparison.OrdinalIgnoreCase))
            return identifier.StartsWith(PrefixNprofile, StringComparison.OrdinalIgnoreCase) && TryDecodeNprofileToHex(identifier, out pubKeyHex);

        try
        {
            pubKeyHex = NpubToHexPub(identifier);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<JObject?> FetchFirstFromRelaysAsync(IEnumerable<NostrFilter> filters, TimeSpan timeout, Func<JObject, bool>? validate = null)
    {
        if (adminSettingsCache.NostrRelays.Length == 0)
            throw new ArgumentException("Provide at least one relay");
        var filterList = filters as IList<NostrFilter> ?? filters.ToList();

        using var cts = new CancellationTokenSource(timeout);
        var token = cts.Token;

        var tasks = adminSettingsCache.NostrRelays.Select(async relay =>
        {
            try
            {
                var list = await FetchEventsFromRelayAsync(relay, filterList, token, 1).ConfigureAwait(false);
                var ev = list.Count > 0 ? list[0] : null;
                if (ev is null)
                    return null;
                if (validate is not null && !validate(ev))
                    return null;
                return ev;
            }
            catch { return null; }
        }).ToList();

        while (tasks.Count > 0 && !token.IsCancellationRequested)
        {
            var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(finished);
            var ev = await finished.ConfigureAwait(false);
            if (ev is null)
                continue;

            await cts.CancelAsync();
            return ev;
        }

        return null;
    }

    private static async Task<IReadOnlyList<JObject>> FetchEventsFromRelayAsync(string relayUrl, IList<NostrFilter> filters, CancellationToken token,
        int expected = 1)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await ws.ConnectAsync(new Uri(relayUrl), token);

        var subId = Guid.NewGuid().ToString("N");
        var req = new List<object> { "REQ", subId };
        req.AddRange(filters);
        var reqBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(req));
        await ws.SendAsync(reqBytes, WebSocketMessageType.Text, true, token);

        var results = new List<JObject>(Math.Max(1, expected));
        var buffer = new byte[16 * 1024];

        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(buffer, token);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    break;
                }

                ms.Write(buffer, 0, res.Count);
            } while (!res.EndOfMessage);

            var msg = Encoding.UTF8.GetString(ms.ToArray());
            if (string.IsNullOrWhiteSpace(msg))
                continue;

            if (JArray.Parse(msg) is not { Count: >= 2 } arr)
                continue;
            var typ = arr[0].ToString();
            var sid = arr[1].ToString();

            if (typ == "EVENT" && sid == subId && arr.Count >= 3 && arr[2] is JObject ev)
            {
                results.Add(ev);
                if (results.Count < expected)
                    continue;

                var closeReq = JsonConvert.SerializeObject(new object[] { "CLOSE", subId });
                await ws.SendAsync(Encoding.UTF8.GetBytes(closeReq), WebSocketMessageType.Text, true, CancellationToken.None);
                break;
            }

            if (typ == "EOSE" && sid == subId)
                break;
        }

        return results;
    }

    public Task<JObject?> FetchEventFromRelaysAsync(string eventIdHex, int timeoutMs = 8000)
    {
        if (string.IsNullOrWhiteSpace(eventIdHex))
            throw new ArgumentNullException(nameof(eventIdHex));
        var filters = new[] { new NostrFilter { Ids = new[] { eventIdHex }, Limit = 1 } };
        return FetchFirstFromRelaysAsync(filters, TimeSpan.FromMilliseconds(timeoutMs),
            ev => string.Equals((string?)ev["id"], eventIdHex, StringComparison.OrdinalIgnoreCase));
    }

    private Task<JObject?> FetchKind0FromRelaysAsync(string authorPubkeyHex, int timeoutMs = 6000)
    {
        if (string.IsNullOrWhiteSpace(authorPubkeyHex))
            throw new ArgumentNullException(nameof(authorPubkeyHex));
        var filters = new[] { new NostrFilter { Kinds = new[] { 0 }, Authors = new[] { authorPubkeyHex }, Limit = 1 } };
        return FetchFirstFromRelaysAsync(filters, TimeSpan.FromMilliseconds(timeoutMs),
            ev => ev["kind"]?.Value<int>() == 0 && string.Equals((string?)ev["pubkey"], authorPubkeyHex, StringComparison.OrdinalIgnoreCase));
    }

    public static string? ExtractNip19(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        s = s.Trim();

        if (IsHex64(s) || s.StartsWith("note1", StringComparison.OrdinalIgnoreCase) || s.StartsWith("nevent1", StringComparison.OrdinalIgnoreCase))
            return s;

        var match = _nip19Regex.Match(s);
        return match.Success ? match.Value : null;
    }

    public static bool IsHex64(string s)
    {
        return s.Length == 64 && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    }

    public static bool TryDecodeNoteToEventIdHex(string note, out string? eventIdHex)
    {
        eventIdHex = null;
        try
        {
            var bytes = ConvertBits(Bech32DecodeTo5(PrefixNote, note), 5, 8, false);
            if (bytes.Length != 32)
                return false;
            eventIdHex = Convert.ToHexString(bytes).ToLowerInvariant();
            return true;
        }
        catch { return false; }
    }

    public static bool TryDecodeNeventToEventIdHex(string nevent, out string? eventIdHex)
    {
        eventIdHex = null;
        try
        {
            var tlvBytes = ConvertBits(Bech32DecodeTo5(PrefixNevent, nevent), 5, 8, false);
            for (var i = 0; i + 2 <= tlvBytes.Length;)
            {
                var type = tlvBytes[i++];
                var length = tlvBytes[i++];
                if (i + length > tlvBytes.Length)
                    break;
                if (type == 0 && length == 32)
                {
                    eventIdHex = Convert.ToHexString(tlvBytes.AsSpan(i, length)).ToLowerInvariant();
                    return true;
                }

                i += length;
            }

            return false;
        }
        catch { return false; }
    }

    public static bool TryDecodeNprofileToHex(string nprofile, out string? authorPubKeyHex)
    {
        authorPubKeyHex = null;
        try
        {
            var tlvBytes = ConvertBits(Bech32DecodeTo5(PrefixNprofile, nprofile), 5, 8, false);
            for (var i = 0; i + 2 <= tlvBytes.Length;)
            {
                var type = tlvBytes[i++];
                var length = tlvBytes[i++];
                if (i + length > tlvBytes.Length)
                    break;

                if (type == 0 && length == 32)
                {
                    authorPubKeyHex = Convert.ToHexString(tlvBytes.AsSpan(i, length)).ToLowerInvariant();
                    return true;
                }

                i += length;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Bech32DecodeTo5(string hrp, string bech)
    {
        return Encoders.Bech32(hrp).DecodeDataRaw(bech.Trim(), out _);
    }

    private static byte[] ConvertBits(byte[] data, int fromBits, int toBits, bool pad)
    {
        int acc = 0, bits = 0, maxV = (1 << toBits) - 1;
        var ret = new List<byte>();
        foreach (var v in data)
        {
            if (v >> fromBits > 0)
                throw new ArgumentOutOfRangeException(nameof(data));
            acc = (acc << fromBits) | v;
            bits += fromBits;
            while (bits >= toBits)
            {
                bits -= toBits;
                ret.Add((byte)((acc >> bits) & maxV));
            }
        }

        if (pad)
        {
            if (bits > 0)
                ret.Add((byte)((acc << (toBits - bits)) & maxV));
        }
        else if (bits >= fromBits || ((acc << (toBits - bits)) & maxV) != 0)
        {
            throw new FormatException("invalid padding");
        }

        return ret.ToArray();
    }

    public async Task<NostrProfileCache?> GetNostrProfileByAuthorHexAsync(string authorPubKeyHex, int timeoutPerRelayMs = 6000)
    {
        var kind0 = await FetchKind0FromRelaysAsync(authorPubKeyHex, timeoutPerRelayMs);
        if (kind0 is null)
            return null;
        var profile = ParseKind0ToProfile(kind0);

        if (!string.IsNullOrWhiteSpace(profile.PictureUrl) && Uri.TryCreate(profile.PictureUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return profile;

        profile.PictureUrl = null;
        return profile;
    }

    private static NostrProfileCache ParseKind0ToProfile(JObject kind0)
    {
        const int maxContentLength = 64_000;
        var contentJson = kind0.Value<string>("content");
        string? picture = null;
        string? display = null;
        string? name = null;

        if (!string.IsNullOrWhiteSpace(contentJson) && contentJson.Length <= maxContentLength)
            try
            {
                using var doc = JsonDocument.Parse(contentJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("picture", out var picEl) && picEl.ValueKind == JsonValueKind.String)
                    picture = picEl.GetString();

                if (root.TryGetProperty("display_name", out var dnEl) && dnEl.ValueKind == JsonValueKind.String)
                    display = dnEl.GetString();

                if (root.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    name = nameEl.GetString();
            }
            catch (JsonException) { }

        return new NostrProfileCache
        {
            PictureUrl = picture,
            Name = display ?? name,
            LastUpdatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }
}

public class NostrEvent
{
    [JsonProperty("kind")]
    public int Kind { get; set; }

    [JsonProperty("created_at")]
    public long CreatedAt { get; set; }

    [JsonProperty("content")]
    public string Content { get; set; } = "";

    [JsonProperty("pubkey")]
    public string Pubkey { get; set; } = ""; // 32-byte hex

    [JsonProperty("id")]
    public string Id { get; set; } = ""; // 32-byte hex

    [JsonProperty("sig")]
    public string Sig { get; set; } = ""; // 64-byte hex

    [JsonProperty("tags")]
    public List<string[]> Tags { get; set; } = new();
}

public record NostrChallenge(string Token, string UserId, DateTimeOffset ExpiresAt);

public record StartNip07Response(string ChallengeToken, string Message);

public record VerifyNip07Request(string ChallengeToken, NostrEvent Event);

public sealed class NostrFilter
{
    [JsonProperty("ids", NullValueHandling = NullValueHandling.Ignore)]
    public string[]? Ids { get; init; }

    [JsonProperty("kinds", NullValueHandling = NullValueHandling.Ignore)]
    public int[]? Kinds { get; init; }

    [JsonProperty("authors", NullValueHandling = NullValueHandling.Ignore)]
    public string[]? Authors { get; init; }

    [JsonProperty("limit", NullValueHandling = NullValueHandling.Ignore)]
    public int? Limit { get; init; }
}
