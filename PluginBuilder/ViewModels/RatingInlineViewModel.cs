namespace PluginBuilder.ViewModels;

public class RatingInlineViewModel
{
    public decimal Average { get; set; }
    public int TotalReviews { get; set; }
    public string IconSize { get; set; } = "fs-5";
    public bool ShowNumber { get; set; } = true;
    public string? AriaLabelPrefix { get; set; }
    public bool SingleStar { get; set; }
    public bool ShowTotalReviews { get; set; }
}
