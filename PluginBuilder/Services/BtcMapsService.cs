using System.Net.Http.Headers;
using System.Text;
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

        var description = (request.Description ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(description) || description.Length > 1000)
            errors.Add(new ValidationError(nameof(request.Description), "Required, 1-1000 characters."));

        if (request.SubmitToDirectory)
        {
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
            if (request.OsmNodeId is null or <= 0)
                errors.Add(new ValidationError(nameof(request.OsmNodeId),
                    "Required for OSM tagging. Must be positive."));

            var nodeType = (request.OsmNodeType ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(nodeType) || !ValidOsmNodeTypes.Contains(nodeType))
                errors.Add(new ValidationError(nameof(request.OsmNodeType),
                    $"Required for OSM tagging. One of: {string.Join(", ", ValidOsmNodeTypes)}."));
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

        var updatedJson = JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }) + "\n";

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
        var nodeType = request.OsmNodeType!.ToLowerInvariant();
        var nodeId = request.OsmNodeId!.Value;

        using var client = new HttpClient { BaseAddress = new Uri(apiBase) };
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var changesetXml = new XDocument(
            new XElement("osm",
                new XElement("changeset",
                    new XElement("tag", new XAttribute("k", "created_by"), new XAttribute("v", UserAgent)),
                    new XElement("tag", new XAttribute("k", "comment"),
                        new XAttribute("v", $"Tag {request.Name} as accepting bitcoin via BTCPay Server")),
                    new XElement("tag", new XAttribute("k", "source"), new XAttribute("v", "BTCPay Server plugin-builder")))));

        var csResponse = await client.PutAsync("changeset/create",
            new StringContent(changesetXml.ToString(), Encoding.UTF8, "text/xml"), cancellationToken);
        csResponse.EnsureSuccessStatusCode();
        var changesetId = long.Parse(await csResponse.Content.ReadAsStringAsync(cancellationToken));

        try
        {
            var elementPath = $"{nodeType}/{nodeId}";
            var elementXmlText = await client.GetStringAsync(elementPath, cancellationToken);
            var elementDoc = XDocument.Parse(elementXmlText);
            var elementEl = elementDoc.Root?.Element(nodeType)
                ?? throw new InvalidOperationException($"OSM element <{nodeType}> not found in response");

            elementEl.SetAttributeValue("changeset", changesetId);

            SetOsmTag(elementEl, "payment:bitcoin", "yes");
            if (!string.IsNullOrWhiteSpace(request.Url))
                SetOsmTag(elementEl, "website", request.Url);
            SetOsmTag(elementEl, "payment:lightning", "yes");

            var putResponse = await client.PutAsync(elementPath,
                new StringContent(elementDoc.ToString(), Encoding.UTF8, "text/xml"), cancellationToken);
            putResponse.EnsureSuccessStatusCode();
            var newVersion = int.Parse(await putResponse.Content.ReadAsStringAsync(cancellationToken));

            return new BtcMapsOsmResult
            {
                ChangesetId = changesetId,
                NodeId = nodeId,
                NodeType = nodeType,
                NewVersion = newVersion
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

    private static void SetOsmTag(XElement element, string key, string value)
    {
        var existing = element.Elements("tag").FirstOrDefault(t => (string?)t.Attribute("k") == key);
        if (existing is not null)
            existing.SetAttributeValue("v", value);
        else
            element.Add(new XElement("tag", new XAttribute("k", key), new XAttribute("v", value)));
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
            if (!string.IsNullOrWhiteSpace(request.Country))
                w.WriteString("country", request.Country.Trim());
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

    private static string BuildPrBody(BtcMapsSubmitRequest request, string urlMarker)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Automated submission from the BTCPay Server plugin-builder `/apis/btcmaps/v1/submit` endpoint.");
        sb.AppendLine();
        sb.AppendLine($"- **Name:** {request.Name}");
        sb.AppendLine($"- **URL:** {request.Url}");
        sb.AppendLine($"- **Type:** {request.Type}{(string.IsNullOrWhiteSpace(request.SubType) ? string.Empty : " / " + request.SubType)}");
        if (!string.IsNullOrWhiteSpace(request.Country)) sb.AppendLine($"- **Country:** {request.Country}");
        if (!string.IsNullOrWhiteSpace(request.Twitter)) sb.AppendLine($"- **Twitter:** {request.Twitter}");
        if (!string.IsNullOrWhiteSpace(request.Github)) sb.AppendLine($"- **GitHub:** {request.Github}");
        sb.AppendLine();
        sb.AppendLine("**Description:**");
        sb.AppendLine(request.Description);
        sb.AppendLine();
        sb.AppendLine("_Please review before merge — this PR was opened programmatically by a BTCMap-plugin merchant submission, not by a maintainer._");
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
