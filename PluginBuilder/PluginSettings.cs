#nullable disable
using System.ComponentModel.DataAnnotations;

namespace PluginBuilder;

public class PluginSettings
{
    // Keep UTF-8 title keys within PostgreSQL's B-tree index entry limit.
    public const int MaxTitleLength = 500;

    public static bool IsTitleTooLong(string title) =>
        title is not null && title.Trim().EnumerateRunes().Skip(MaxTitleLength).Any();

    [MaxLength(200)]
    [Display(Name = "Documentation link")]
    public string Documentation { get; set; }

    [MaxLength(200)]
    [Display(Name = "Git repository")]
    public string GitRepository { get; set; }

    [MaxLength(200)]
    [Display(Name = "Git branch or tag")]
    public string GitRef { get; set; }

    [MaxLength(200)]
    [Display(Name = "Directory to the plugin's project")]
    public string PluginDirectory { get; set; }

    [MaxLength(200)]
    [Display(Name = "Dotnet build configuration ")]
    public string BuildConfig { get; set; }

    public string PluginTitle { get; set; }
    public string Description { get; set; }
    public string Logo { get; set; }

    [MaxLength(200)]
    [Display(Name = "Video URL")]
    public string VideoUrl { get; set; }

    public List<string> Images { get; set; } = [];
    public bool RequireGPGSignatureForRelease { get; set; }
}
