namespace AudioScript.Services;

public enum DependencyHealthCategory
{
    Asset,
    PythonModule,
}

public enum DependencyHealthStatus
{
    Pending,
    Checking,
    Downloading,
    Installing,
    Retrying,
    Completed,
    Failed,
    Skipped,
}

public sealed record DependencyRepairAttempt(
    string Strategy,
    int Attempt,
    bool Succeeded,
    int? ExitCode,
    string Message);

public sealed record DependencyHealthItem(
    string Id,
    string DisplayName,
    DependencyHealthCategory Category,
    DependencyHealthStatus Status,
    string Message,
    string Impact,
    IReadOnlyList<DependencyRepairAttempt> Attempts);

public sealed record StartupDependencyHealthProgress(
    string DependencyId,
    string DisplayName,
    DependencyHealthCategory Category,
    DependencyHealthStatus Status,
    string Message,
    double Percent,
    int Attempt,
    int MaxAttempts);

public sealed record StartupDependencyHealthResult(
    bool Succeeded,
    bool Degraded,
    IReadOnlyList<DependencyHealthItem> FailedItems,
    IReadOnlyList<DependencyRepairAttempt> AttemptedRepairs);

public sealed record PythonDependencyRepairResult(
    bool Succeeded,
    IReadOnlyList<DependencyHealthItem> Items,
    IReadOnlyList<DependencyRepairAttempt> Attempts);

public interface IPythonDependencyRepairService
{
    Task<PythonDependencyRepairResult> ValidateAndRepairAsync(
        IProgress<StartupDependencyHealthProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IStartupDependencyHealthCoordinator
{
    Task<StartupDependencyHealthResult> RunAsync(
        IProgress<StartupDependencyHealthProgress>? progress,
        CancellationToken cancellationToken);
}
