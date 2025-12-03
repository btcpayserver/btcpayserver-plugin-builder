using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace PluginBuilder.ViewModels.Admin;

public class ImportReviewViewModel
{
    public enum ImportReviewSourceEnum
    {
        Nostr = 1, X, Github, WWW
    }

    [Range(1, 5)]
    public int Rating { get; set; } = 5;
    public string? PluginSlug { get; set; }
    public ImportReviewSourceEnum Source { get; set; } = ImportReviewSourceEnum.Nostr;
    public string? SourceUrl { get; set; }
    public string? Body { get; set; }
    public bool LinkExistingUser { get; set; } = true;
    public string? SelectedUserId { get; set; }
    public string? ReviewerName { get; set; }
    public string? ReviewerAvatarUrl { get; set; }
    public string? ReviewerProfileUrl { get; set; }
    public List<SelectListItem>? ExistingUsers { get; set; }
    public string? WwwDisplayName { get; set; }
    public string? WwwAvatarUrl { get; set; }
}
