namespace AudioScript.Services;

internal static class UpdateLogMetadata
{
    public static string Build(
        string operation,
        string state,
        string? installedVersion,
        string? availableVersion,
        int updateCount,
        bool mandatory,
        int failedPackageCount,
        Exception? exception = null,
        string? extra = null)
    {
        string metadata =
            $"operation={Normalize(operation)}; " +
            $"state={Normalize(state)}; " +
            $"installedVersion={Normalize(installedVersion)}; " +
            $"availableVersion={Normalize(availableVersion)}; " +
            $"updateCount={Math.Max(0, updateCount)}; " +
            $"mandatory={mandatory}; " +
            $"failedPackageCount={Math.Max(0, failedPackageCount)}; " +
            $"exceptionType={Normalize(exception?.GetType().Name)}; " +
            $"exceptionMessage={Normalize(exception?.Message)}";

        if (!string.IsNullOrWhiteSpace(extra))
        {
            metadata += $"; {extra.Trim()}";
        }

        return metadata;
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Trim();
    }
}
