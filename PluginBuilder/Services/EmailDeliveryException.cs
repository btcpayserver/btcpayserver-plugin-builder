namespace PluginBuilder.Services;

/// <summary>Preserves SMTP submission progress when a batch stops before all recipients are accepted.</summary>
public sealed class EmailDeliveryException(
    IReadOnlyList<string> acceptedRecipients,
    IReadOnlyList<string> unsentRecipients,
    string? uncertainRecipient,
    Exception innerException)
    : Exception($"Email sending stopped after {acceptedRecipients.Count} recipient(s) were accepted by the mail server.", innerException)
{
    public IReadOnlyList<string> AcceptedRecipients { get; } = acceptedRecipients.ToArray();

    /// <summary>Addresses explicitly rejected by the server or not yet attempted.</summary>
    public IReadOnlyList<string> UnsentRecipients { get; } = unsentRecipients.ToArray();

    /// <summary>An address whose submission may have succeeded without a confirmation reaching the client.</summary>
    public string? UncertainRecipient { get; } = uncertainRecipient;
}
