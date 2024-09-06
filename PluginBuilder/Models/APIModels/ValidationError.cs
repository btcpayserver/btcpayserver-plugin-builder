namespace PluginBuilder.APIModels;

public class ValidationError
{
    public ValidationError(string path, string message)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }

    public string Path { get; set; }
    public string Message { get; set; }
}
