using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PluginBuilder.APIModels;
using PluginBuilder.DataModels;

namespace PluginBuilder.Services;

public sealed class BtcMapsService
{
    private const string DefaultDirectoryRepo = "btcpayserver/directory.btcpayserver.org";
    private const string DefaultDirectoryMerchantsPath = "src/data/merchants.json";

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

    // ISO 3166-1 alpha-2 codes derived from CultureInfo at startup. Cached because
    // CultureInfo.GetCultures + RegionInfo enumeration is non-trivial and the set
    // is stable for the process lifetime.
    private static readonly HashSet<string> Iso3166Alpha2 = BuildIsoAlpha2Set();

    private static HashSet<string> BuildIsoAlpha2Set()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                if (region.TwoLetterISORegionName.Length == 2 &&
                    region.TwoLetterISORegionName.All(c => c is >= 'A' and <= 'Z'))
                {
                    set.Add(region.TwoLetterISORegionName);
                }
            }
            catch (ArgumentException)
            {
                // Some neutral cultures throw; skip.
            }
        }
        return set;
    }

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BtcMapsService> _logger;

    public BtcMapsService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<BtcMapsService> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public sealed class DirectoryTokenMissingException : Exception
    {
        public DirectoryTokenMissingException() : base("BTCMAPS:DirectoryGithubToken is not configured.") { }
    }

    public sealed class BtcMapTokenMissingException : Exception
    {
        public BtcMapTokenMissingException() : base("BTCMAPS:BtcMapImportToken is not configured.") { }
    }

    private const string DefaultBtcMapImportEndpoint = "https://api.btcmap.org/rpc";
    private const string BtcMapImportOrigin = "btcpayserver";
    private const string BtcMapImportMethod = "submit_place";

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
            // GLOBAL is the directory's pseudonym for online-only / multi-region merchants.
            // Everything else must be an actual ISO 3166-1 alpha-2 code.
            if (country != "GLOBAL" && !Iso3166Alpha2.Contains(country))
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

        // BTC Map import RPC fields become mandatory only when the caller
        // opts into that lane via SubmitToBtcMap=true. Directory-only callers
        // (the existing PR #224 shape) are unaffected.
        if (request.SubmitToBtcMap)
        {
            if (!request.Lat.HasValue || request.Lat.Value is < -90 or > 90 || double.IsNaN(request.Lat.Value))
                errors.Add(new ValidationError(nameof(request.Lat),
                    "Required when SubmitToBtcMap=true. Must be in [-90, 90]."));

            if (!request.Lon.HasValue || request.Lon.Value is < -180 or > 180 || double.IsNaN(request.Lon.Value))
                errors.Add(new ValidationError(nameof(request.Lon),
                    "Required when SubmitToBtcMap=true. Must be in [-180, 180]."));

            var category = (request.Category ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(category) || category.Length > 50 ||
                !category.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
            {
                errors.Add(new ValidationError(nameof(request.Category),
                    "Required when SubmitToBtcMap=true. Short lowercase identifier (a-z, 0-9, -, _; max 50 chars)."));
            }

            var externalId = (request.ExternalId ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(externalId) || externalId.Length > 200)
                errors.Add(new ValidationError(nameof(request.ExternalId),
                    "Required when SubmitToBtcMap=true. 1-200 characters."));
        }

        return errors;
    }

    public async Task<BtcMapsBtcMapResult> SubmitToBtcMapAsync(
        BtcMapsSubmitRequest request,
        CancellationToken cancellationToken = default)
    {
        var token = _configuration["BTCMAPS:BtcMapImportToken"];
        if (string.IsNullOrWhiteSpace(token))
            throw new BtcMapTokenMissingException();

        // Bearer tokens MUST NOT cross the wire over http://; an operator-
        // misconfigured endpoint would silently leak the scoped token to
        // anyone on the network path. Parse the configured value into an
        // absolute https URI before we even create the request, so a bad
        // BTCMAPS:BtcMapImportEndpoint fails loudly with the offending
        // value in the message instead of producing a quiet credential leak.
        var endpoint = (_configuration["BTCMAPS:BtcMapImportEndpoint"] ?? DefaultBtcMapImportEndpoint).Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            endpointUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"BTCMAPS:BtcMapImportEndpoint must be an absolute https:// URL (got '{endpoint}').");
        }

        var client = _httpClientFactory.CreateClient(HttpClientNames.BtcMap);

        // BTC Map import-RPC takes a JSON-RPC 2.0 envelope at /rpc with method=submit_place.
        // Required params: origin, external_id, lat, lon, category, name. extra_fields uses
        // the documented first-class keys (phone, website, description) and the osm:<tag_name>
        // convention for granular OSM tags (osm:addr:*).
        var extraFields = new Dictionary<string, object?>();
        // First-class btcmap fields (plain keys per rest/v4/places.md).
        if (!string.IsNullOrWhiteSpace(request.Url)) extraFields["website"] = request.Url.Trim();
        if (!string.IsNullOrWhiteSpace(request.Description)) extraFields["description"] = request.Description.Trim();
        if (!string.IsNullOrWhiteSpace(request.Phone)) extraFields["phone"] = request.Phone.Trim();
        if (!string.IsNullOrWhiteSpace(request.Email)) extraFields["email"] = request.Email.Trim();
        if (!string.IsNullOrWhiteSpace(request.Twitter))
        {
            // btcmap's `twitter` field is documented as a URL. Normalize the @handle
            // shape the rest of the API accepts (with or without leading @) into
            // the URL form the directory map expects.
            var t = request.Twitter.Trim();
            var handle = t.StartsWith("@") ? t[1..] : t;
            extraFields["twitter"] = handle.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? handle
                : $"https://x.com/{handle}";
        }
        // OSM custom tags use osm:<tag_name> per the same doc.
        if (!string.IsNullOrWhiteSpace(request.HouseNumber)) extraFields["osm:addr:housenumber"] = request.HouseNumber.Trim();
        if (!string.IsNullOrWhiteSpace(request.Street)) extraFields["osm:addr:street"] = request.Street.Trim();
        if (!string.IsNullOrWhiteSpace(request.City)) extraFields["osm:addr:city"] = request.City.Trim();
        if (!string.IsNullOrWhiteSpace(request.Postcode)) extraFields["osm:addr:postcode"] = request.Postcode.Trim();
        if (!string.IsNullOrWhiteSpace(request.Country)) extraFields["osm:addr:country"] = request.Country.Trim();
        // Payment-rail flags. Plugin sets per the store's enabled rails - omit
        // when null or false so a Lightning-only store doesn't claim on-chain
        // support (or vice versa).
        if (request.AcceptsOnchain == true) extraFields["payment:onchain"] = "yes";
        if (request.AcceptsLightning == true) extraFields["payment:lightning"] = "yes";

        var rpcParams = new Dictionary<string, object?>
        {
            ["origin"] = BtcMapImportOrigin,
            ["external_id"] = request.ExternalId!.Trim(),
            ["lat"] = request.Lat!.Value,
            ["lon"] = request.Lon!.Value,
            ["category"] = request.Category!.Trim().ToLowerInvariant(),
            ["name"] = request.Name!.Trim(),
            ["extra_fields"] = extraFields
        };

        var envelope = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = BtcMapImportMethod,
            ["params"] = rpcParams,
            ["id"] = 1
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, endpointUri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(req, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"BtcMap RPC {(int)response.StatusCode} {endpointUri}: {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // JSON-RPC 2.0 response shape: either {result: {...}} on success or {error: {...}}.
        // 2xx status with an error body is a legal JSON-RPC outcome we must surface.
        if (root.TryGetProperty("error", out var errorElement))
        {
            var errorJson = errorElement.GetRawText();
            throw new HttpRequestException($"BtcMap RPC error response: {errorJson}");
        }

        if (!root.TryGetProperty("result", out var result))
            throw new HttpRequestException($"BtcMap RPC missing result: {body}");

        return new BtcMapsBtcMapResult
        {
            Id = result.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : null,
            Origin = result.TryGetProperty("origin", out var origin) ? origin.GetString() : null,
            ExternalId = result.TryGetProperty("external_id", out var ext) ? ext.GetString() : null
        };
    }

    public async Task<BtcMapsDirectoryResult> SubmitToDirectoryAsync(
        BtcMapsSubmitRequest request,
        CancellationToken cancellationToken = default)
    {
        var token = _configuration["BTCMAPS:DirectoryGithubToken"];
        if (string.IsNullOrWhiteSpace(token))
            throw new DirectoryTokenMissingException();

        var repo = _configuration["BTCMAPS:DirectoryRepo"] ?? DefaultDirectoryRepo;
        var merchantsPath = _configuration["BTCMAPS:DirectoryMerchantsPath"] ?? DefaultDirectoryMerchantsPath;

        var client = _httpClientFactory.CreateClient(HttpClientNames.BtcMapsDirectory);
        // Auth is per-call: the named-client registration sets BaseAddress + User-Agent
        // + Accept + Timeout, but the BTCMAPS token is distinct from the global
        // PluginBuilder GitHub token and must not be baked into the singleton handler.
        using var authClient = new HttpRequestAuth(client, token);

        var repoInfo = await GetJsonAsync(authClient, $"repos/{repo}", cancellationToken);
        var defaultBranch = repoInfo.GetProperty("default_branch").GetString()
            ?? throw new InvalidOperationException("default_branch missing");

        var fileInfo = await GetJsonAsync(
            authClient,
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

        // Deterministic branch name derived from the normalized URL. Two concurrent
        // submissions of the same URL collide on the git/refs create instead of
        // racing through preflight and opening duplicate PRs.
        var branchName = BuildBranchName(request.Name!, normalizedUrl);
        var marker = BuildUrlMarker(normalizedUrl);

        var branchRef = await GetJsonAsync(
            authClient,
            $"repos/{repo}/git/ref/heads/{Uri.EscapeDataString(defaultBranch)}",
            cancellationToken);
        var baseSha = branchRef.GetProperty("object").GetProperty("sha").GetString()
            ?? throw new InvalidOperationException("base sha missing");

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

        var refCreateResponse = await PostJsonAllowConflictAsync(
            authClient,
            $"repos/{repo}/git/refs",
            new { @ref = $"refs/heads/{branchName}", sha = baseSha },
            cancellationToken);
        if (refCreateResponse.IsConflict)
        {
            // Branch already exists. Look up the open PR keyed by the URL marker;
            // if one is open, return its details; otherwise this is a stuck-branch
            // from a prior failed run and we cannot safely reuse it.
            var openPrSearch = await GetJsonAsync(
                authClient,
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
            return new BtcMapsDirectoryResult { Skipped = "branch-exists-no-open-pr", Branch = branchName };
        }

        await PutJsonAsync(authClient, $"repos/{repo}/contents/{merchantsPath}",
            new
            {
                message = $"Add {request.Name}",
                content = Convert.ToBase64String(Encoding.UTF8.GetBytes(updatedJson)),
                sha = fileSha,
                branch = branchName
            }, cancellationToken);

        var prBody = BuildPrBody(request, marker);
        var prResponse = await PostJsonAsync(authClient, $"repos/{repo}/pulls",
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
            // type + subType are validated case-insensitively above
            // (ValidTypes / ValidMerchantSubTypes use OrdinalIgnoreCase) but
            // the upstream merchants.json convention is lowercase. Normalize
            // on write so a submission of "Merchants" / "Books" lands as
            // "merchants" / "books" in the file. Country uses a case-sensitive
            // Ordinal set so the validator already rejects non-uppercase.
            w.WriteString("type", request.Type!.Trim().ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(request.SubType))
                w.WriteString("subType", request.SubType.Trim().ToLowerInvariant());
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
        // User-supplied fields are wrapped in inline code spans so a doctored merchant
        // name (e.g. `[click here](https://attacker.example)`) can't render as a clickable
        // link in the maintainer-facing PR description. The URL is its own line and is
        // displayed via a plain markdown link with a sanitized label so the maintainer
        // sees the bare URL, not a renamed target.
        var sb = new StringBuilder();
        sb.AppendLine("Automated submission from the BTCPay Server plugin-builder `/apis/btcmaps/v1/submit` endpoint.");
        sb.AppendLine();
        sb.AppendLine($"- **Name:** {EscapeInlineCode(request.Name)}");
        sb.AppendLine($"- **URL:** <{request.Url}>");
        sb.AppendLine($"- **Type:** {EscapeInlineCode(request.Type)}{(string.IsNullOrWhiteSpace(request.SubType) ? string.Empty : " / " + EscapeInlineCode(request.SubType))}");
        if (!string.IsNullOrWhiteSpace(request.Country)) sb.AppendLine($"- **Country:** {EscapeInlineCode(request.Country.Trim())}");
        if (!string.IsNullOrWhiteSpace(request.Twitter))
        {
            // The Twitter handle is rendered inside an inline code span so a hostile
            // value like `]( <evil-url> )` cannot escape into an active link. The
            // maintainer can copy the handle and visit manually.
            var raw = request.Twitter.Trim();
            var handle = raw.StartsWith("@") ? raw[1..] : raw;
            sb.AppendLine($"- **Twitter:** {EscapeInlineCode("@" + handle)}");
        }
        if (!string.IsNullOrWhiteSpace(request.Github)) sb.AppendLine($"- **GitHub:** {EscapeInlineCode(request.Github)}");
        sb.AppendLine();
        sb.AppendLine("**Description:**");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(request.Description?.Replace("```", "``​`") ?? string.Empty);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("_Please review before merge - this PR was opened programmatically by a BTCMap-plugin merchant submission, not by a maintainer._");
        sb.AppendLine();
        sb.AppendLine($"<!-- {urlMarker} -->");
        return sb.ToString();
    }

    // Wrap user input as an inline code span. Use enough backticks to escape any
    // backticks the input contains (CommonMark inline-code-fence rule).
    private static string EscapeInlineCode(string? value)
    {
        var s = value ?? string.Empty;
        if (s.Length == 0) return "``";
        var longestRun = 0;
        var current = 0;
        foreach (var c in s)
        {
            if (c == '`') { current++; if (current > longestRun) longestRun = current; }
            else current = 0;
        }
        var fence = new string('`', longestRun + 1);
        // Pad with spaces if the value starts or ends with a backtick (CommonMark rule).
        var needsPad = s.StartsWith("`") || s.EndsWith("`");
        return needsPad ? $"{fence} {s} {fence}" : $"{fence}{s}{fence}";
    }

    private static string BuildUrlMarker(string normalizedUrl) =>
        $"btcmaps-submit:url={normalizedUrl}";

    public static string NormalizeUrl(string url)
    {
        // Normalize for duplicate detection without lying about case-sensitive parts.
        // Scheme + host get lower-cased (DNS is case-insensitive, scheme is too); path
        // and query are preserved as-is. Trailing slash is trimmed only when the path
        // is the bare root, since /foo/ and /foo are sometimes distinct on real servers.
        var trimmed = (url ?? string.Empty).Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
            return trimmed.TrimEnd('/');
        var sb = new StringBuilder();
        sb.Append(parsed.Scheme.ToLowerInvariant());
        sb.Append("://");
        sb.Append(parsed.Host.ToLowerInvariant());
        if (!parsed.IsDefaultPort)
        {
            sb.Append(':');
            sb.Append(parsed.Port);
        }
        var path = parsed.AbsolutePath;
        if (path == "/")
            sb.Append(path);
        else
            sb.Append(path.TrimEnd('/'));
        if (!string.IsNullOrEmpty(parsed.Query))
            sb.Append(parsed.Query);
        return sb.ToString();
    }

    // Deterministic branch name from the normalized URL. Same URL always produces
    // the same branch; second concurrent submission collides on the git/refs create
    // and the controller surfaces the duplicate-open-PR shape.
    public static string BuildBranchName(string name, string normalizedUrl)
    {
        var slug = Slugify(name);
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(normalizedUrl));
        var suffix = Convert.ToHexString(hash)[..8].ToLowerInvariant();
        return $"btcmaps/{slug}-{suffix}";
    }

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

    // Lightweight wrapper that re-attaches the per-request Authorization header on
    // each call. The named-client handler is reused (socket pool, factory rotation),
    // but auth stays out of the singleton handler.
    private sealed class HttpRequestAuth : IDisposable
    {
        public HttpClient Client { get; }
        public string Token { get; }
        public HttpRequestAuth(HttpClient client, string token) { Client = client; Token = token; }
        public void Dispose() { /* HttpClient is owned by IHttpClientFactory; do not dispose */ }
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static async Task<JsonElement> GetJsonAsync(HttpRequestAuth auth, string path, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Get, path, auth.Token);
        using var response = await auth.Client.SendAsync(req, ct);
        await EnsureSuccess(response, path, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> PostJsonAsync(HttpRequestAuth auth, string path, object body, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Post, path, auth.Token);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await auth.Client.SendAsync(req, ct);
        await EnsureSuccess(response, path, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private readonly record struct ConflictAware(JsonElement? Body, bool IsConflict);

    private static async Task<ConflictAware> PostJsonAllowConflictAsync(HttpRequestAuth auth, string path, object body, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Post, path, auth.Token);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await auth.Client.SendAsync(req, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
        {
            // GitHub returns 422 "Reference already exists" when the branch ref is
            // pre-claimed by a concurrent or earlier submission. That's the idempotency
            // signal we want; surface it.
            var conflictText = await response.Content.ReadAsStringAsync(ct);
            if (conflictText.Contains("Reference already exists", StringComparison.OrdinalIgnoreCase))
                return new ConflictAware(null, true);
            throw new HttpRequestException($"GitHub {(int)response.StatusCode} {path}: {conflictText}");
        }
        await EnsureSuccess(response, path, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        return new ConflictAware(doc.RootElement.Clone(), false);
    }

    private static async Task<JsonElement> PutJsonAsync(HttpRequestAuth auth, string path, object body, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Put, path, auth.Token);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await auth.Client.SendAsync(req, ct);
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
