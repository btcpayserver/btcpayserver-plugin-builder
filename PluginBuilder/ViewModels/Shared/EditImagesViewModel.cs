namespace PluginBuilder.ViewModels.Shared;

public class EditImagesViewModel
{
    public List<string> ExistingImages { get; init; } = [];

    public static EditImagesViewModel Create(List<string>? images)
    {
        return new EditImagesViewModel
        {
            ExistingImages = images?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? []
        };
    }
}
