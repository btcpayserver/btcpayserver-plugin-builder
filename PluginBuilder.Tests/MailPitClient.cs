using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace PluginBuilder.Tests;

/// <summary>
/// Minimal client for the Mailpit HTTP API (https://mailpit.axllent.org/docs/api-v1/).
/// Trimmed to the fields our tests assert on. Runs against the mailpit service defined in
/// PluginBuilder.Tests/docker-compose.yml.
/// </summary>
public class MailPitClient
{
    private readonly HttpClient _client;

    public MailPitClient(HttpClient client)
    {
        _client = client;
    }

    /// <summary>Fetch a single captured message by its Mailpit id.</summary>
    public async Task<Message> GetMessage(string id)
    {
        var json = await _client.GetStringAsync($"api/v1/message/{id}");
        return JsonConvert.DeserializeObject<Message>(json)!;
    }

    /// <summary>
    /// Search captured messages (Mailpit query syntax, e.g. "subject:... to:...").
    /// Tests poll this by subject to find a message after triggering a send.
    /// </summary>
    public async Task<SearchResult> Search(string query)
    {
        var json = await _client.GetStringAsync($"api/v1/search?query={System.Uri.EscapeDataString(query)}");
        return JsonConvert.DeserializeObject<SearchResult>(json)!;
    }

    public sealed class SearchResult
    {
        [JsonProperty("messages")]
        public List<MessageSummary> Messages { get; set; } = new();
    }

    public sealed class MessageSummary
    {
        [JsonProperty("ID")]
        public string Id { get; set; } = "";

        [JsonProperty("Subject")]
        public string Subject { get; set; } = "";
    }

    public sealed class Message
    {
        [JsonProperty("ID")]
        public string Id { get; set; } = "";

        [JsonProperty("Subject")]
        public string Subject { get; set; } = "";

        [JsonProperty("Text")]
        public string Text { get; set; } = "";

        [JsonProperty("HTML")]
        public string Html { get; set; } = "";

        [JsonProperty("From")]
        public MailAddress? From { get; set; }

        [JsonProperty("To")]
        public List<MailAddress> To { get; set; } = new();
    }

    public sealed class MailAddress
    {
        [JsonProperty("Address")]
        public string Address { get; set; } = "";

        [JsonProperty("Name")]
        public string Name { get; set; } = "";
    }
}
