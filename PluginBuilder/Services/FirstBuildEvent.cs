namespace PluginBuilder.Services;

public class FirstBuildEvent(EmailService emailService, ILogger<FirstBuildEvent> logger)
{
    private string? _baseUrl;

    public void InitBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return;
        if (!baseUrl.StartsWith("http://") && !baseUrl.StartsWith("https://"))
            return;

        if (_baseUrl is null)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            logger.LogInformation("FirstBuildEvent cached base URL: {BaseUrl}", _baseUrl);
        }
    }
}
