namespace PluginBuilder.DataModels;

public class ConfirmModel
{
    private const string ButtonClassDefault = "btn-danger";

    public ConfirmModel() { }

    public ConfirmModel(
        string title,
        string desc,
        string? action = null,
        string buttonClass = ButtonClassDefault,
        bool descriptionHtml = false,
        string? actionName = null,
        string? controllerName = null)
    {
        Title = title;
        Description = desc;
        Action = action;
        ActionName = actionName;
        ControllerName = controllerName;
        ButtonClass = buttonClass;
        DescriptionHtml = descriptionHtml;
    }

    public bool GenerateForm { get; set; } = true;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool DescriptionHtml { get; set; }
    public string? Action { get; set; }
    public string? ActionName { get; set; }
    public string? ControllerName { get; set; }
    public string ButtonClass { get; set; } = ButtonClassDefault;
}
