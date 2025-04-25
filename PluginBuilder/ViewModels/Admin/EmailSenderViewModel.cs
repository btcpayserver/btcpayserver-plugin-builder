using System.ComponentModel.DataAnnotations;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
namespace PluginBuilder.ViewModels.Admin;

public class EmailSenderViewModel
{
    [Required]
    [Display(Name = "Recipient Email")]
    public string To { get; set; }

    [Required]
    [EmailAddress]
    [Display(Name = "Sender Email")]
    public string From { get; set; }

    [Required]
    [Display(Name = "Email Subject")]
    public string Subject { get; set; }

    [Required]
    [Display(Name = "Email Message")]
    public string Message { get; set; }
}
