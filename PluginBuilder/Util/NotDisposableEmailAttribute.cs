using System.ComponentModel.DataAnnotations;

namespace PluginBuilder.Util;

/// <summary>Refuses an email address on a domain listed in <see cref="DisposableEmailDomains"/>.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NotDisposableEmailAttribute() : ValidationAttribute("Please provide a valid email address.")
{
    public override bool IsValid(object? value) => value is not string email || !DisposableEmailDomains.IsDisposable(email);
}
