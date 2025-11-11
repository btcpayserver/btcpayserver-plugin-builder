using System.ComponentModel.DataAnnotations;

namespace PluginBuilder.ViewModels.Account;

public class VerifyNostrManualViewModel
{
    [Display(Name = "Note URL or NIP-19 (nevent/note1…)")]
    [Required]
    public string NoteRef { get; set; } = string.Empty;
    public string ChallengeToken { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
