namespace AudioScript.Services;

public enum AppUpdateState
{
    Idle,
    Checking,
    UpdateAvailable,
    Downloading,
    Installing,
    Completed,
    Deferred,
    Failed,
    Skipped,
}

public enum StoreUpdateOperationState
{
    Completed,
    Canceled,
    ErrorLowBattery,
    OtherError,
    Unknown,
}

public sealed record AppUpdateSnapshot(
    AppUpdateState State,
    string StageText,
    string StatusMessage,
    bool IsMandatoryUpdateAvailable,
    bool IsProgressVisible,
    double ProgressValue,
    string InstalledVersion,
    string? AvailableVersion)
{
    public static AppUpdateSnapshot Idle(string installedVersion) =>
        new(
            AppUpdateState.Idle,
            "Up to date",
            string.Empty,
            IsMandatoryUpdateAvailable: false,
            IsProgressVisible: false,
            ProgressValue: 0,
            InstalledVersion: installedVersion,
            AvailableVersion: null);
}

public sealed record StorePackageUpdateInfo(
    string PackageFamilyName,
    string PackageFullName,
    string Version,
    bool IsMandatory);

public sealed record PackageIdentitySnapshot(
    string PackageFamilyName,
    string PackageFullName,
    string PackageVersion);

public sealed class StorePackageUpdateSet
{
    public StorePackageUpdateSet(IReadOnlyList<StorePackageUpdateInfo> updates, object? nativeUpdates = null)
    {
        Updates = updates ?? throw new ArgumentNullException(nameof(updates));
        NativeUpdates = nativeUpdates;
    }

    public IReadOnlyList<StorePackageUpdateInfo> Updates { get; }

    public object? NativeUpdates { get; }

    public bool HasUpdates => Updates.Count > 0;
}

public sealed record StoreUpdateQueryResult(
    StorePackageUpdateSet UpdateSet,
    bool CanSilentlyDownload);

public sealed record StoreUpdateOperationProgress(double ProgressValue);

public sealed record StoreUpdateOperationResult(
    StoreUpdateOperationState State,
    int FailedPackageCount = 0,
    string? ErrorMessage = null,
    IReadOnlyList<string>? FailedPackageFamilyNames = null)
{
    public bool Succeeded => State == StoreUpdateOperationState.Completed;

    public bool Cancelled => State == StoreUpdateOperationState.Canceled;
}

public sealed class DeferredUpdateState
{
    public DateTimeOffset? LastCheckUtc { get; init; }

    public DateTimeOffset? LastUpdateDetectedUtc { get; init; }

    public DateTimeOffset? LastDownloadCompletedUtc { get; init; }

    public DateTimeOffset? LastInstallDeferredUtc { get; init; }

    public DateTimeOffset? LastFailureUtc { get; init; }

    public DateTimeOffset? LastSuccessfulOperationUtc { get; init; }

    public DateTimeOffset? LastAttemptUtc { get; init; }

    public bool InstallDeferred { get; init; }

    public int RetryCount { get; init; }

    public string? LastFailureCategory { get; init; }

    public string? LastFailureMessage { get; init; }

    public PackageIdentitySnapshot? PackageIdentitySnapshot { get; init; }

    public IReadOnlyList<string> PackageFamilyNames { get; init; } = Array.Empty<string>();
}

public sealed class StoreUpdateOptions
{
    public bool EnableStartupUpdateCheck { get; init; } = true;

    public bool PreferSilentUpdateWhenAvailable { get; init; } = true;

    public bool UseFallbackStoreUiWhenSilentUnavailable { get; init; } = true;

    public bool ShowProgressDuringFallbackUi { get; init; } = true;

    public bool RestartAppAutomatically { get; init; }

    public TimeSpan StartupDelay { get; init; } = TimeSpan.Zero;

    public TimeSpan MinimumCheckInterval { get; init; } = TimeSpan.Zero;

    public TimeSpan DeferredStateMaxAge { get; init; } = TimeSpan.FromDays(14);

    public TimeSpan ExitInstallTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public int ExitInstallRetryCountLimit { get; init; } = 3;

    public TimeSpan ExitInstallRetryCooldown { get; init; } = TimeSpan.FromDays(1);
}

public interface IAppVersionProvider
{
    bool IsPackaged { get; }

    string InstalledVersion { get; }
}

public interface IAppUpdateService : IAsyncDisposable
{
    AppUpdateSnapshot CurrentSnapshot { get; }

    event EventHandler<AppUpdateSnapshot>? SnapshotChanged;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task RunUserInitiatedUpdateFlowAsync(CancellationToken cancellationToken = default);

    Task<StoreUpdateOperationResult?> RunExitTimeInstallAsync(
        CancellationToken cancellationToken = default);

    Task<bool> HasDeferredInstallOnExitAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}

public interface IAppUpdateCoordinator
{
    Task RunStartupUpdateFlowAsync(CancellationToken cancellationToken = default);

    Task RunUserInitiatedUpdateFlowAsync(CancellationToken cancellationToken = default);

    Task<StoreUpdateOperationResult?> RunExitTimeInstallAsync(
        CancellationToken cancellationToken = default);

    Task<bool> HasDeferredInstallOnExitAsync(CancellationToken cancellationToken = default);
}

public interface IMicrosoftStoreUpdateProvider
{
    bool IsStoreUpdateSupported();

    Task<StoreUpdateQueryResult> GetAvailableUpdatesAsync(CancellationToken cancellationToken = default);

    bool CanSilentlyDownloadUpdates(StoreUpdateQueryResult queryResult);

    Task<StoreUpdateOperationResult> TrySilentDownloadAsync(
        StorePackageUpdateSet updateSet,
        Action<StoreUpdateOperationProgress>? progress,
        CancellationToken cancellationToken = default);

    Task<StoreUpdateOperationResult> TrySilentDownloadAndInstallAsync(
        StorePackageUpdateSet updateSet,
        Action<StoreUpdateOperationProgress>? progress,
        CancellationToken cancellationToken = default);

    Task<StoreUpdateOperationResult> RequestDownloadAndInstallWithStoreUiAsync(
        StorePackageUpdateSet updateSet,
        Action<StoreUpdateOperationProgress>? progress,
        CancellationToken cancellationToken = default);
}

public interface IDeferredUpdateStateStore
{
    Task<DeferredUpdateState?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(DeferredUpdateState state, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
