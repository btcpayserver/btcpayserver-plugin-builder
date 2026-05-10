using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PluginBuilder.APIModels;

namespace PluginBuilder.Services;

public sealed class BtcMapsService
{
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

        var type = (request.Type ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(type) || !ValidTypes.Contains(type))
            errors.Add(new ValidationError(nameof(request.Type),
                $"Required. One of: {string.Join(", ", ValidTypes)}."));

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
        if (!string.IsNullOrWhiteSpace(request.Country)) sb.AppendLine($"- **Country:** {request.Country.Trim()}");
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
