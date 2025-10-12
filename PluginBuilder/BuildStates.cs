namespace PluginBuilder;

public enum BuildStates
{
    Queued,
    Uploaded,
    Uploading,
    Removed,
    Running,
    Failed,
    WaitingUpload
}

public static class BuildStatesExtensions
{
    public static string ToEventName(this BuildStates buildState)
    {
        return buildState switch
        {
            BuildStates.Queued => "queued",
            BuildStates.Removed => "removed",
            BuildStates.Running => "running",
            BuildStates.Failed => "failed",
            BuildStates.Uploaded => "uploaded",
            BuildStates.Uploading => "uploading",
            BuildStates.WaitingUpload => "waiting-upload",
            _ => throw new ArgumentOutOfRangeException(nameof(buildState), buildState, null)
        };
    }

    public static BuildStates FromEventName(string? eventName)
    {
        return eventName switch
        {
            "queued" => BuildStates.Queued,
            "removed" => BuildStates.Removed,
            "running" => BuildStates.Running,
            "failed" => BuildStates.Failed,
            "uploaded" => BuildStates.Uploaded,
            "uploading" => BuildStates.Uploading,
            "waiting-upload" => BuildStates.WaitingUpload,
            _ => throw new ArgumentOutOfRangeException(nameof(eventName), eventName, null)
        };
    }

    public static bool IsTerminal(this BuildStates s) =>
        s is BuildStates.Uploaded or BuildStates.Failed or BuildStates.Removed;
}
