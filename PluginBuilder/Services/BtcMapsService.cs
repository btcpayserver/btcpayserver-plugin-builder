using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using PluginBuilder.APIModels;

namespace PluginBuilder.Services;

public sealed class BtcMapsService
{
    private const string DefaultOsmApiBase = "https://api.openstreetmap.org/api/0.6/";
    private const string DefaultDirectoryRepo = "btcpayserver/directory.btcpayserver.org";
    private const string DefaultDirectoryMerchantsPath = "src/data/merchants.json";
    private const string UserAgent = "PluginBuilder-BtcMaps/1.0";

    private static readonly HashSet<string> ValidTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "merchants", "apps", "hosted-btcpay", "non-profits"
    };

    private static readonly HashSet<string> ValidMerchantSubTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "3d-printing", "adult", "appliances-furniture", "art", "books",
        "cryptocurrency-paraphernalia", "domains-hosting-vpns", "education",
        "electronics", "fashion", "food", "gambling", "gift-cards",
        "health-household", "holiday-travel", "jewelry", "payment-services",
        "pets", "services", "software-video-games", "sports", "tools"
    };

    private static readonly HashSet<string> ValidOsmNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "way", "relation"
    };

    private readonly IConfiguration _configuration;
    private readonly ILogger<BtcMapsService> _logger;

    public BtcMapsService(
        IConfiguration configuration,
        ILogger<BtcMapsService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public IReadOnlyList<ValidationError> Validate(BtcMapsSubmitRequest request)
    {
        var errors = new List<ValidationError>();

        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
            errors.Add(new ValidationError(nameof(request.Name), "Required, 1-200 characters."));

        var url = (request.Url ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(url))
            errors.Add(new ValidationError(nameof(request.Url), "Required."));
        else if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
            errors.Add(new ValidationError(nameof(request.Url), "Must be a valid https:// URL."));

        if (request.SubmitToDirectory)
        {
            // Description is only consumed by the directory PR body; not required for
            // tagOnOsm-only or unlistFromOsm-only requests.
            var description = (request.Description ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(description) || description.Length > 1000)
                errors.Add(new ValidationError(nameof(request.Description), "Required, 1-1000 characters."));

            var type = (request.Type ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(type) || !ValidTypes.Contains(type))
                errors.Add(new ValidationError(nameof(request.Type),
                    $"Required for directory submission. One of: {string.Join(", ", ValidTypes)}."));

            if (!string.IsNullOrWhiteSpace(request.SubType))
            {
                var subType = request.SubType.Trim();
                if (string.Equals(type, "merchants", StringComparison.OrdinalIgnoreCase) &&
                    !ValidMerchantSubTypes.Contains(subType))
                {
                    errors.Add(new ValidationError(nameof(request.SubType),
                        "Invalid merchant subtype."));
                }
            }

            if (!string.IsNullOrWhiteSpace(request.Country))
            {
                var country = request.Country.Trim();
                if (!(country == "GLOBAL" || (country.Length == 2 && country.All(char.IsUpper))))
                    errors.Add(new ValidationError(nameof(request.Country),
                        "Must be ISO 3166-1 alpha-2 or GLOBAL."));
            }

            if (!string.IsNullOrWhiteSpace(request.OnionUrl))
            {
                if (!Uri.TryCreate(request.OnionUrl.Trim(), UriKind.Absolute, out var onionUri) ||
                    (onionUri.Scheme != Uri.UriSchemeHttp && onionUri.Scheme != Uri.UriSchemeHttps) ||
                    !onionUri.Host.EndsWith(".onion", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new ValidationError(nameof(request.OnionUrl),
                        "Must be an http(s) .onion URL."));
                }
            }
        }

        if (request.TagOnOsm)
        {
            if (request.OsmNodeId is null)
            {
                // Create-new path: lat + lon required; OsmNodeType defaults to "node"
                // when the service creates the OSM element.
                if (request.Latitude is null || request.Latitude < -90.0 || request.Latitude > 90.0)
                    errors.Add(new ValidationError(nameof(request.Latitude),
                        "Required when OsmNodeId is null. Must be in range [-90, 90]."));
                if (request.Longitude is null || request.Longitude < -180.0 || request.Longitude > 180.0)
                    errors.Add(new ValidationError(nameof(request.Longitude),
                        "Required when OsmNodeId is null. Must be in range [-180, 180]."));
            }
            else
            {
                if (request.OsmNodeId <= 0)
                    errors.Add(new ValidationError(nameof(request.OsmNodeId),
                        "Must be positive."));

                var nodeType = (request.OsmNodeType ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(nodeType) || !ValidOsmNodeTypes.Contains(nodeType))
                    errors.Add(new ValidationError(nameof(request.OsmNodeType),
                        $"Required when OsmNodeId is set. One of: {string.Join(", ", ValidOsmNodeTypes)}."));
            }
        }

        if (request.Address is not null && !string.IsNullOrWhiteSpace(request.Address.Country))
        {
            var addrCountry = request.Address.Country.Trim();
            if (!(addrCountry.Length == 2 && addrCountry.All(char.IsUpper)))
                errors.Add(new ValidationError($"{nameof(request.Address)}.{nameof(request.Address.Country)}",
                    "Must be ISO 3166-1 alpha-2."));
        }

        if (request.UnlistFromOsm)
        {
            // Un-listing always targets an existing element - there is no "remove-from-new-node"
            // path. Mutually exclusive with TagOnOsm (opposite intent) and with SubmitToDirectory
            // (v1 scope is OSM-only; directory unlist is a separate flow).
            if (request.TagOnOsm)
                errors.Add(new ValidationError(nameof(request.UnlistFromOsm),
                    "Cannot be combined with tagOnOsm (opposite intent)."));
            if (request.SubmitToDirectory)
                errors.Add(new ValidationError(nameof(request.UnlistFromOsm),
                    "Cannot be combined with submitToDirectory (directory unlist is out of v1 scope)."));

            if (request.OsmNodeId is null || request.OsmNodeId <= 0)
                errors.Add(new ValidationError(nameof(request.OsmNodeId),
                    "Required when unlistFromOsm is true. Must be positive."));

            var nodeType = (request.OsmNodeType ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(nodeType) || !ValidOsmNodeTypes.Contains(nodeType))
                errors.Add(new ValidationError(nameof(request.OsmNodeType),
                    $"Required when unlistFromOsm is true. One of: {string.Join(", ", ValidOsmNodeTypes)}."));
        }

        return errors;
    }

    public async Task<BtcMapsDirectoryResult> SubmitToDirectoryAsync(
        BtcMapsSubmitRequest request,
        CancellationToken cancellationToken = default)
    {
        var token = _configuration["BTCMAPS:DirectoryGithubToken"];
        if (string.IsNullOrWhiteSpace(token))
            return new BtcMapsDirectoryResult { Skipped = "directory-github-token-not-configured" };

        var repo = _configuration["BTCMAPS:DirectoryRepo"] ?? DefaultDirectoryRepo;
        var merchantsPath = _configuration["BTCMAPS:DirectoryMerchantsPath"] ?? DefaultDirectoryMerchantsPath;

        using var client = new HttpClient();
        client.BaseAddress = new Uri("https://api.github.com/");
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var repoInfo = await GetJsonAsync(client, $"repos/{repo}", cancellationToken);
        var defaultBranch = repoInfo.GetProperty("default_branch").GetString()
            ?? throw new InvalidOperationException("default_branch missing");

        var fileInfo = await GetJsonAsync(
            client,
            $"repos/{repo}/contents/{merchantsPath}?ref={Uri.EscapeDataString(defaultBranch)}",
            cancellationToken);
        var contentB64 = fileInfo.GetProperty("content").GetString() ?? string.Empty;
        var fileSha = fileInfo.GetProperty("sha").GetString() ?? string.Empty;
        var currentJson = Encoding.UTF8.GetString(Convert.FromBase64String(contentB64.Replace("\n", string.Empty)));

        var merchants = JsonSerializer.Deserialize<List<JsonElement>>(currentJson)
            ?? throw new InvalidOperationException("merchants.json must be a JSON array");

        var normalizedUrl = NormalizeUrl(request.Url!);
        foreach (var existing in merchants)
        {
            if (existing.TryGetProperty("url", out var existingUrl) &&
                existingUrl.ValueKind == JsonValueKind.String &&
                NormalizeUrl(existingUrl.GetString() ?? string.Empty) == normalizedUrl)
            {
                var existingName = existing.TryGetProperty("name", out var n) ? n.GetString() : "(unknown)";
                return new BtcMapsDirectoryResult { Skipped = $"duplicate-url:{existingName}" };
            }
        }

        var marker = BuildUrlMarker(normalizedUrl);
        var openPrSearch = await GetJsonAsync(
            client,
            $"search/issues?q={Uri.EscapeDataString($"repo:{repo} is:pr is:open in:body \"{marker}\"")}",
            cancellationToken);
        if (openPrSearch.TryGetProperty("total_count", out var totalCount) && totalCount.GetInt32() > 0)
        {
            var firstItem = openPrSearch.GetProperty("items")[0];
            return new BtcMapsDirectoryResult
            {
                Skipped = "duplicate-open-pr",
                PrUrl = firstItem.TryGetProperty("html_url", out var h) ? h.GetString() : null,
                PrNumber = firstItem.TryGetProperty("number", out var n) ? n.GetInt32() : null
            };
        }

        var newEntry = BuildMerchantEntry(request);
        var updated = merchants
            .Select(e => (JsonElement?)e)
            .Append(newEntry)
            .OrderBy(e => e!.Value.TryGetProperty("name", out var n) ? n.GetString() : string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .Select(e => e!.Value)
            .ToList();

        // Use UnsafeRelaxedJsonEscaping so non-ASCII codepoints and HTML-only "unsafe"
        // chars (`&`, `'`, `<`, `>`) are written raw in the file, matching the upstream
        // merchants.json convention. The default JavaScriptEncoder is HTML-safe and
        // would re-encode every entry containing `'` or non-ASCII as `\uXXXX`, which
        // shows up as a noisy full-file diff on every append.
        var updatedJson = JsonSerializer.Serialize(updated, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }) + "\n";

        var branchRef = await GetJsonAsync(
            client,
            $"repos/{repo}/git/ref/heads/{Uri.EscapeDataString(defaultBranch)}",
            cancellationToken);
        var baseSha = branchRef.GetProperty("object").GetProperty("sha").GetString()
            ?? throw new InvalidOperationException("base sha missing");

        var branchSuffix = Guid.NewGuid().ToString("N")[..8];
        var branchName = $"btcmaps/{Slugify(request.Name!)}-{branchSuffix}";
        await PostJsonAsync(client, $"repos/{repo}/git/refs",
            new { @ref = $"refs/heads/{branchName}", sha = baseSha }, cancellationToken);

        await PutJsonAsync(client, $"repos/{repo}/contents/{merchantsPath}",
            new
            {
                message = $"Add {request.Name}",
                content = Convert.ToBase64String(Encoding.UTF8.GetBytes(updatedJson)),
                sha = fileSha,
                branch = branchName
            }, cancellationToken);

        var prBody = BuildPrBody(request, marker);
        var prResponse = await PostJsonAsync(client, $"repos/{repo}/pulls",
            new
            {
                title = $"Add {request.Name}",
                head = branchName,
                @base = defaultBranch,
                body = prBody
            }, cancellationToken);

        return new BtcMapsDirectoryResult
        {
            PrUrl = prResponse.GetProperty("html_url").GetString(),
            PrNumber = prResponse.GetProperty("number").GetInt32(),
            Branch = branchName
        };
    }

    public async Task<BtcMapsOsmResult> TagOnOsmAsync(
        BtcMapsSubmitRequest request,
        CancellationToken cancellationToken = default)
    {
        var token = _configuration["BTCMAPS:OsmAccessToken"];
        if (string.IsNullOrWhiteSpace(token))
            return new BtcMapsOsmResult { Skipped = "osm-access-token-not-configured" };

        var apiBase = _configuration["BTCMAPS:OsmApiBase"] ?? DefaultOsmApiBase;
        var isCreate = request.OsmNodeId is null;
        var nodeType = isCreate ? "node" : request.OsmNodeType!.ToLowerInvariant();

        using var client = new HttpClient { BaseAddress = new Uri(apiBase) };
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var changesetComment = isCreate
            ? $"Add {request.Name} as a bitcoin-accepting place via BTCPay Server #btcmap"
            : $"Tag {request.Name} as accepting bitcoin via BTCPay Server #btcmap";
        var changesetXml = new XDocument(
            new XElement("osm",
                new XElement("changeset",
                    new XElement("tag", new XAttribute("k", "created_by"), new XAttribute("v", UserAgent)),
                    new XElement("tag", new XAttribute("k", "comment"), new XAttribute("v", changesetComment)),
                    new XElement("tag", new XAttribute("k", "source"), new XAttribute("v", "BTCPay Server plugin-builder")))));

        var csResponse = await client.PutAsync("changeset/create",
            new StringContent(changesetXml.ToString(), Encoding.UTF8, "text/xml"), cancellationToken);
        csResponse.EnsureSuccessStatusCode();
        var changesetId = long.Parse(await csResponse.Content.ReadAsStringAsync(cancellationToken));

        try
        {
            long nodeId;
            int newVersion;

            if (isCreate)
            {
                // Build a brand-new <node> with the merchant's tags. OSM accepts the
                // POST /api/0.6/node body as <osm><node ...><tag .../></node></osm>
                // and returns the freshly-allocated node ID as plain text.
                var amenity = string.IsNullOrWhiteSpace(request.OsmCategory)
                    ? "shop"
                    : request.OsmCategory.Trim();

                var newNode = new XElement("node",
                    new XAttribute("changeset", changesetId),
                    new XAttribute("lat", request.Latitude!.Value.ToString("R", CultureInfo.InvariantCulture)),
                    new XAttribute("lon", request.Longitude!.Value.ToString("R", CultureInfo.InvariantCulture)));
                newNode.Add(new XElement("tag", new XAttribute("k", "name"), new XAttribute("v", request.Name!.Trim())));
                newNode.Add(new XElement("tag", new XAttribute("k", "amenity"), new XAttribute("v", amenity)));
                newNode.Add(new XElement("tag", new XAttribute("k", "currency:XBT"), new XAttribute("v", "yes")));
                // BTC Map verification stamp - bumped on every tag operation per
                // https://gitea.btcmap.org/teambtcmap/btcmap-general/wiki/Verifying-Existing-Merchants
                // Date-only UTC; the act of submitting through the plugin is itself the verification.
                newNode.Add(new XElement("tag", new XAttribute("k", "check_date:currency:XBT"), new XAttribute("v", TodayUtcDate())));
                if (!string.IsNullOrWhiteSpace(request.Url))
                    newNode.Add(new XElement("tag", new XAttribute("k", "website"), new XAttribute("v", request.Url.Trim())));
                if (request.AcceptsLightning)
                    newNode.Add(new XElement("tag", new XAttribute("k", "payment:lightning"), new XAttribute("v", "yes")));
                AddAddressTagsToNewNode(newNode, request.Address);

                var createDoc = new XDocument(new XElement("osm", newNode));
                var createResponse = await client.PutAsync("node/create",
                    new StringContent(createDoc.ToString(), Encoding.UTF8, "text/xml"), cancellationToken);
                createResponse.EnsureSuccessStatusCode();
                nodeId = long.Parse(await createResponse.Content.ReadAsStringAsync(cancellationToken));
                newVersion = 1;
            }
            else
            {
                nodeId = request.OsmNodeId!.Value;
                var elementPath = $"{nodeType}/{nodeId}";
                var elementXmlText = await client.GetStringAsync(elementPath, cancellationToken);
                var elementDoc = XDocument.Parse(elementXmlText);
                var elementEl = elementDoc.Root?.Element(nodeType)
                    ?? throw new InvalidOperationException($"OSM element <{nodeType}> not found in response");

                elementEl.SetAttributeValue("changeset", changesetId);

                // Bitcoin acceptance: per OSM, payment:bitcoin=yes is deprecated in favor
                // of currency:XBT=yes (XBT is ISO 4217). Lightning is gated on the
                // request's AcceptsLightning flag (per-store config).
                SetOsmTag(elementEl, "currency:XBT", "yes");
                // BTC Map verification stamp - same date-only UTC stamp as the create
                // path, bumped here on re-verify or on any tag-update flow.
                SetOsmTag(elementEl, "check_date:currency:XBT", TodayUtcDate());
                if (!string.IsNullOrWhiteSpace(request.Url))
                    SetOsmTag(elementEl, "website", request.Url);
                if (request.AcceptsLightning)
                    SetOsmTag(elementEl, "payment:lightning", "yes");
                ApplyAddressTags(elementEl, request.Address);

                var putResponse = await client.PutAsync(elementPath,
                    new StringContent(elementDoc.ToString(), Encoding.UTF8, "text/xml"), cancellationToken);
                putResponse.EnsureSuccessStatusCode();
                newVersion = int.Parse(await putResponse.Content.ReadAsStringAsync(cancellationToken));
            }

            return new BtcMapsOsmResult
            {
                ChangesetId = changesetId,
                NodeId = nodeId,
                NodeType = nodeType,
                NewVersion = newVersion,
                Created = isCreate
            };
        }
        finally
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await client.PutAsync($"changeset/{changesetId}/close",
                    new StringContent(string.Empty), closeCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to close OSM changeset {ChangesetId}", changesetId);
            }
        }
    }

    // Bitcoin-acceptance tags this service removes when un-listing. Keeps `website`,
    // `name`, `amenity`, and address tags intact since those are not bitcoin-specific
    // (a venue may remain on OSM after it stops accepting bitcoin). payment:bitcoin
    // is included for historical nodes tagged before the deprecation-vs-currency:XBT
    // switch.
    private static readonly string[] BitcoinAcceptanceTagKeys =
    {
        "currency:XBT",
        "payment:bitcoin",
        "payment:lightning",
        "payment:onchain"
    };

    public async Task<BtcMapsOsmResult> UnlistFromOsmAsync(
        BtcMapsSubmitRequest request,
        CancellationToken cancellationToken = default)
    {
        var token = _configuration["BTCMAPS:OsmAccessToken"];
        if (string.IsNullOrWhiteSpace(token))
            return new BtcMapsOsmResult { Skipped = "osm-access-token-not-configured" };

        var apiBase = _configuration["BTCMAPS:OsmApiBase"] ?? DefaultOsmApiBase;
        var nodeType = request.OsmNodeType!.ToLowerInvariant();
        var nodeId = request.OsmNodeId!.Value;
        var elementPath = $"{nodeType}/{nodeId}";

        using var client = new HttpClient { BaseAddress = new Uri(apiBase) };
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Fetch first so we can skip the changeset entirely when the element already
        // has none of the bitcoin-related tags we remove. Idempotency + 409 surface.
        var elementXmlText = await client.GetStringAsync(elementPath, cancellationToken);
        var elementDoc = XDocument.Parse(elementXmlText);
        var elementEl = elementDoc.Root?.Element(nodeType)
            ?? throw new InvalidOperationException($"OSM element <{nodeType}> not found in response");

        var removableKeys = BitcoinAcceptanceTagKeys
            .Where(k => elementEl.Elements("tag").Any(t => (string?)t.Attribute("k") == k))
            .ToArray();

        if (removableKeys.Length == 0)
        {
            // Nothing to remove - the element already carries no bitcoin-acceptance
            // tags we own. Report it so the controller surfaces 409 to the plugin
            // (distinguishes idempotent no-op from "removed just now").
            return new BtcMapsOsmResult
            {
                NodeId = nodeId,
                NodeType = nodeType,
                Skipped = "already-unlisted"
            };
        }

        var changesetXml = new XDocument(
            new XElement("osm",
                new XElement("changeset",
                    new XElement("tag", new XAttribute("k", "created_by"), new XAttribute("v", UserAgent)),
                    new XElement("tag", new XAttribute("k", "comment"), new XAttribute("v", $"Un-list {request.Name} from bitcoin-accepting places via BTCPay Server #btcmap")),
                    new XElement("tag", new XAttribute("k", "source"), new XAttribute("v", "BTCPay Server plugin-builder")))));

        var csResponse = await client.PutAsync("changeset/create",
            new StringContent(changesetXml.ToString(), Encoding.UTF8, "text/xml"), cancellationToken);
        csResponse.EnsureSuccessStatusCode();
        var changesetId = long.Parse(await csResponse.Content.ReadAsStringAsync(cancellationToken));

        try
        {
            elementEl.SetAttributeValue("changeset", changesetId);
            foreach (var key in removableKeys)
                RemoveOsmTag(elementEl, key);

            var putResponse = await client.PutAsync(elementPath,
                new StringContent(elementDoc.ToString(), Encoding.UTF8, "text/xml"), cancellationToken);
            putResponse.EnsureSuccessStatusCode();
            var newVersion = int.Parse(await putResponse.Content.ReadAsStringAsync(cancellationToken));

            return new BtcMapsOsmResult
            {
                ChangesetId = changesetId,
                NodeId = nodeId,
                NodeType = nodeType,
                NewVersion = newVersion,
                RemovedTags = removableKeys
            };
        }
        finally
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await client.PutAsync($"changeset/{changesetId}/close",
                    new StringContent(string.Empty), closeCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to close OSM changeset {ChangesetId}", changesetId);
            }
        }
    }

    private static void RemoveOsmTag(XElement element, string key)
    {
        var existing = element.Elements("tag").FirstOrDefault(t => (string?)t.Attribute("k") == key);
        existing?.Remove();
    }

    private static void SetOsmTag(XElement element, string key, string value)
    {
        var existing = element.Elements("tag").FirstOrDefault(t => (string?)t.Attribute("k") == key);
        if (existing is not null)
            existing.SetAttributeValue("v", value);
        else
            element.Add(new XElement("tag", new XAttribute("k", key), new XAttribute("v", value)));
    }

    private static string TodayUtcDate() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // OSM addr:* writers. Plugin-side splits the raw merchant address into
    // structured components; the server writes only the keys whose values are
    // populated, never inferring or synthesising. This is the create-path
    // helper (appends new <tag> children to a fresh <node>).
    private static void AddAddressTagsToNewNode(XElement newNode, BtcMapsSubmitAddress? address)
    {
        if (address is null) return;
        foreach (var (key, raw) in EnumerateAddressTags(address))
        {
            var value = raw.Trim();
            if (value.Length == 0) continue;
            newNode.Add(new XElement("tag", new XAttribute("k", key), new XAttribute("v", value)));
        }
    }

    // Update-path helper: applies addr:* via SetOsmTag so existing values get
    // overwritten in-place rather than producing duplicate <tag> children.
    private static void ApplyAddressTags(XElement element, BtcMapsSubmitAddress? address)
    {
        if (address is null) return;
        foreach (var (key, raw) in EnumerateAddressTags(address))
        {
            var value = raw.Trim();
            if (value.Length == 0) continue;
            SetOsmTag(element, key, value);
        }
    }

    private static IEnumerable<(string Key, string Value)> EnumerateAddressTags(BtcMapsSubmitAddress address)
    {
        if (!string.IsNullOrWhiteSpace(address.HouseNumber)) yield return ("addr:housenumber", address.HouseNumber);
        if (!string.IsNullOrWhiteSpace(address.Street))      yield return ("addr:street",      address.Street);
        if (!string.IsNullOrWhiteSpace(address.City))        yield return ("addr:city",        address.City);
        if (!string.IsNullOrWhiteSpace(address.Postcode))    yield return ("addr:postcode",    address.Postcode);
        if (!string.IsNullOrWhiteSpace(address.Country))     yield return ("addr:country",     address.Country);
    }

    private static JsonElement BuildMerchantEntry(BtcMapsSubmitRequest request)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteString("name", request.Name!.Trim());
            w.WriteString("url", request.Url!.Trim());
            w.WriteString("description", request.Description!.Trim());
            w.WriteString("type", request.Type!.Trim());
            if (!string.IsNullOrWhiteSpace(request.SubType))
                w.WriteString("subType", request.SubType.Trim());
            var directoryCountry = ResolveDirectoryCountry(request);
            if (!string.IsNullOrWhiteSpace(directoryCountry))
                w.WriteString("country", directoryCountry);
            if (!string.IsNullOrWhiteSpace(request.Twitter))
            {
                var t = request.Twitter.Trim();
                w.WriteString("twitter", t.StartsWith("@") ? t : "@" + t);
            }
            if (!string.IsNullOrWhiteSpace(request.Github))
                w.WriteString("github", request.Github.Trim());
            if (!string.IsNullOrWhiteSpace(request.OnionUrl))
                w.WriteString("onionUrl", request.OnionUrl.Trim());
            w.WriteEndObject();
        }
        ms.Position = 0;
        using var doc = JsonDocument.Parse(ms);
        return doc.RootElement.Clone();
    }

    // Plugins centralise the merchant's country in one form field. The directory
    // submission consumes top-level `country`; OSM addr:country reads
    // `Address.Country`. If only one is sent, fall back to the other so the
    // merchant.json entry carries the country regardless of which field the
    // plugin populated. Top-level wins because it allows the directory-only
    // GLOBAL pseudonym (e.g. online-only services) which has no OSM addr:*
    // equivalent.
    public static string? ResolveDirectoryCountry(BtcMapsSubmitRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Country))
            return request.Country.Trim();
        if (!string.IsNullOrWhiteSpace(request.Address?.Country))
            return request.Address!.Country!.Trim();
        return null;
    }

    private static string BuildPrBody(BtcMapsSubmitRequest request, string urlMarker)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Automated submission from the BTCPay Server plugin-builder `/apis/btcmaps/v1/submit` endpoint.");
        sb.AppendLine();
        sb.AppendLine($"- **Name:** {request.Name}");
        sb.AppendLine($"- **URL:** {request.Url}");
        sb.AppendLine($"- **Type:** {request.Type}{(string.IsNullOrWhiteSpace(request.SubType) ? string.Empty : " / " + request.SubType)}");
        var prBodyCountry = ResolveDirectoryCountry(request);
        if (!string.IsNullOrWhiteSpace(prBodyCountry)) sb.AppendLine($"- **Country:** {prBodyCountry}");
        if (!string.IsNullOrWhiteSpace(request.Twitter))
        {
            // Render as an explicit https://x.com/<handle> link so GitHub markdown does
            // not auto-resolve a bare `@handle` to github.com/<handle>.
            var raw = request.Twitter.Trim();
            var handle = raw.StartsWith("@") ? raw[1..] : raw;
            sb.AppendLine($"- **Twitter:** [@{handle}](https://x.com/{handle})");
        }
        if (!string.IsNullOrWhiteSpace(request.Github)) sb.AppendLine($"- **GitHub:** {request.Github}");
        sb.AppendLine();
        sb.AppendLine("**Description:**");
        sb.AppendLine(request.Description);
        sb.AppendLine();
        sb.AppendLine("_Please review before merge - this PR was opened programmatically by a BTCMap-plugin merchant submission, not by a maintainer._");
        sb.AppendLine();
        sb.AppendLine($"<!-- {urlMarker} -->");
        return sb.ToString();
    }

    private static string BuildUrlMarker(string normalizedUrl) =>
        $"btcmaps-submit:url={normalizedUrl}";

    public static string NormalizeUrl(string url) =>
        url.Trim().TrimEnd('/').ToLowerInvariant();

    public static string Slugify(string input)
    {
        var chars = new StringBuilder();
        var lastWasDash = true;
        foreach (var c in input.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                chars.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                chars.Append('-');
                lastWasDash = true;
            }
        }
        var result = chars.ToString().Trim('-');
        if (result.Length > 40) result = result[..40].TrimEnd('-');
        return result.Length == 0 ? "merchant" : result;
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path, CancellationToken ct)
    {
        using var response = await client.GetAsync(path, ct);
        await EnsureSuccess(response, path, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string path, object body, CancellationToken ct)
    {
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(path, content, ct);
        await EnsureSuccess(response, path, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> PutJsonAsync(HttpClient client, string path, object body, CancellationToken ct)
    {
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.PutAsync(path, content, ct);
        await EnsureSuccess(response, path, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, string path, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"GitHub {(int)response.StatusCode} {path}: {body}");
    }
}
