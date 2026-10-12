using System.Text.Json;
using PluginBuilder.DataModels;

namespace PluginBuilder.Services;

/// <summary>
/// Cloudflare Turnstile on registration, so a scripted sign-up has to pass a human check. Off unless both
/// PB_TURNSTILE_SITEKEY and PB_TURNSTILE_SECRET are set; when on, a token that cannot be verified is a refusal.
/// </summary>
public interface ITurnstileVerifier
{
    bool Enabled { get; }
    string? SiteKey { get; }
    Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken cancellationToken);
}

public class TurnstileVerifier(IConfiguration configuration, IHttpClientFactory httpClientFactory, ILogger<TurnstileVerifier> logger)
    : ITurnstileVerifier
{
    /// <summary>The hidden field the widget adds to the form it is placed in.</summary>
    public const string ResponseField = "cf-turnstile-response";
    public static readonly Uri SiteVerifyUrl = new("https://challenges.cloudflare.com/turnstile/v0/siteverify");
    private const int MaxTokenLength = 2048;

    private readonly string? _secret = NullIfBlank(configuration["TURNSTILE_SECRET"]);
    public string? SiteKey { get; } = NullIfBlank(configuration["TURNSTILE_SITEKEY"]);
    public bool Enabled => SiteKey is not null && _secret is not null;

    public async Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken cancellationToken)
    {
        if (!Enabled)
            return true;
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
            return false;

        var form = new Dictionary<string, string> { ["secret"] = _secret!, ["response"] = token };
        if (!string.IsNullOrWhiteSpace(remoteIp))
            form["remoteip"] = remoteIp;
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientNames.Turnstile);
            using var response = await client.PostAsync(SiteVerifyUrl, new FormUrlEncodedContent(form), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Turnstile verification returned HTTP {StatusCode}", (int)response.StatusCode);
                return false;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var success = json.RootElement.ValueKind == JsonValueKind.Object &&
                          json.RootElement.TryGetProperty("success", out var value) && value.ValueKind == JsonValueKind.True;
            if (!success)
                logger.LogInformation("Turnstile rejected a registration: {ErrorCodes}", ErrorCodes(json.RootElement));
            return success;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException ||
                                  (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Unreachable, timed out or garbled: the check did not pass, so the registration does not either.
            logger.LogWarning(e, "Turnstile verification failed");
            return false;
        }
    }

    private static string ErrorCodes(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error-codes", out var codes) && codes.ValueKind == JsonValueKind.Array
            ? string.Join(",", codes.EnumerateArray().Select(c => c.ToString()))
            : "";

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
