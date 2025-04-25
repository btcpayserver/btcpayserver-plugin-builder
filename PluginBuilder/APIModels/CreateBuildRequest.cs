using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using PluginBuilder.ViewModels;

namespace PluginBuilder.APIModels;

[ValidateNever] // prevents automatic validation, so that we can apply defaults from settings
public class CreateBuildRequest : CreateBuildViewModel
{
}
