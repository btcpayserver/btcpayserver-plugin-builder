namespace PluginBuilder.ViewModels.Shared;

public class BTCPayCompatibilityModalViewModel
{
    public string ModalId { get; set; } = null!;
    public string ModalLabelId { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string PostUrl { get; set; } = null!;
    public string MinInputId { get; set; } = null!;
    public string MaxInputId { get; set; } = null!;
    public string? MinInputClass { get; set; }
    public string? MaxInputClass { get; set; }
    public string? MinVersion { get; set; }
    public string? MaxVersion { get; set; }
    public bool ShowResetToManifest { get; set; }
}
