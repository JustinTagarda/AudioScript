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
    string Version,
    bool IsMandatory);

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
    IReadOnlyList<string>? FailedPackageFamilyNames = null);

public interface IAppVersionProvider
{
    bool IsPackaged { get; }

    string InstalledVersion { get; }

    string DisplayVersionText { get; }
}

public interface IStoreUpdateClient
{
    Task<StoreUpdateQueryResult> QueryUpdatesAsync(CancellationToken cancellationToken);

    Task<StoreUpdateOperationResult> DownloadUpdatesAsync(
        StorePackageUpdateSet updateSet,
        Action<StoreUpdateOperationProgress>? progress,
        CancellationToken cancellationToken);

    Task<StoreUpdateOperationResult> InstallUpdatesAsync(
        StorePackageUpdateSet updateSet,
        Action<StoreUpdateOperationProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IAppUpdateService : IAsyncDisposable
{
    AppUpdateSnapshot CurrentSnapshot { get; }

    event EventHandler<AppUpdateSnapshot>? SnapshotChanged;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}
