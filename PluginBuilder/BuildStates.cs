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
}
