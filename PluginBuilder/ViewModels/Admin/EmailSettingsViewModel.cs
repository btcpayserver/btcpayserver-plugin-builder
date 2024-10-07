using System.ComponentModel.DataAnnotations;
// ReSharper disable PropertyCanBeMadeInitOnly.Global
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

namespace PluginBuilder.ViewModels.Admin;

public class EmailSettingsViewModel
{
    [Required]
    [Display(Prompt = "smtp.server.com")]
    public string Server { get; set; }

    [Display(Prompt = "Port of SMTP server (usually 465)")]
    public int Port { get; set; }

    [Required]
    [Display(Prompt = "Username on SMTP server")]
    public string Username { get; set; }

    [Required]
    [Display(Prompt = "Password on SMTP server")]
    public string Password { get; set; }

    [Required]
    [Display(Prompt = "Provide from email in format: Full Name <email@server.com>")]
    public string From { get; set; }

    public bool DisableCertificateCheck { get; set; }
}
