namespace PluginBuilder.ViewModels;

public readonly record struct ServiceResult(bool Success, string? Error = null)
{
    public static ServiceResult Ok()
    {
        return new ServiceResult(true);
    }

    public static ServiceResult Fail(string error)
    {
        return new ServiceResult(false, error);
    }
}

public readonly record struct ServiceResult<T>(bool Success, T? Value = default, string? Error = null)
{
    public static ServiceResult<T> Ok(T value)
    {
        return new ServiceResult<T>(true, value);
    }

    public static ServiceResult<T> Fail(string error)
    {
        return new ServiceResult<T>(false, default, error);
    }
}
