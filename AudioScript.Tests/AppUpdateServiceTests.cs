using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class AppUpdateServiceTests
{
    [Fact]
    public async Task RunOnceAsync_Unpackaged_SkipsStoreOperations()
    {
        var provider = new FakeMicrosoftStoreUpdateProvider(isSupported: false);
        await using var service = CreateService(isPackaged: false, provider);

        await service.RunOnceAsync();

        Assert.Equal(AppUpdateState.Idle, service.CurrentSnapshot.State);
        Assert.Equal(0, provider.QueryCount);
    }

    [Fact]
    public async Task RunOnceAsync_NoUpdates_ReturnsIdle()
    {
        var provider = new FakeMicrosoftStoreUpdateProvider
        {
            QueryHandler = _ => Task.FromResult(QueryResult(hasUpdates: false, canSilentlyDownload: true)),
        };
        await using var service = CreateService(isPackaged: true, provider);

        await service.RunOnceAsync();

        Assert.Equal(AppUpdateState.Idle, service.CurrentSnapshot.State);
        Assert.Equal("1.2.3.4", service.CurrentSnapshot.InstalledVersion);
        Assert.Equal(1, provider.QueryCount);
    }

    [Fact]
    public async Task RunOnceAsync_UpdateAvailableWithoutSilentDownload_UsesFallbackStoreUi()
    {
        var provider = new FakeMicrosoftStoreUpdateProvider
        {
            QueryHandler = _ => Task.FromResult(QueryResult(hasUpdates: true, canSilentlyDownload: false)),
        };
        await using var service = CreateService(isPackaged: true, provider);

        await service.RunOnceAsync();

        Assert.Equal(AppUpdateState.Idle, service.CurrentSnapshot.State);
        Assert.Equal(0, provider.DownloadCount);
        Assert.Equal(1, provider.StoreUiCount);
    }

    [Fact]
    public async Task RunOnceAsync_DownloadsAndInstallsSilentlyWhenIdle()
    {
        var provider = new FakeMicrosoftStoreUpdateProvider();
        await using var service = CreateService(isPackaged: true, provider);
        List<AppUpdateState> states = new();
        service.SnapshotChanged += (_, snapshot) => states.Add(snapshot.State);

        await service.RunOnceAsync();

        Assert.Contains(AppUpdateState.Downloading, states);
        Assert.Equal(1, provider.DownloadCount);
        Assert.Equal(1, provider.InstallCount);
        Assert.Equal(AppUpdateState.Idle, service.CurrentSnapshot.State);
    }

    [Fact]
    public async Task RunOnceAsync_CheckFailure_PublishesFailedAndRetries()
    {
        var provider = new FakeMicrosoftStoreUpdateProvider();
        provider.QueryHandler = _ =>
        {
            if (provider.QueryCount == 1)
            {
                throw new InvalidOperationException("query failed");
            }

            return Task.FromResult(QueryResult(hasUpdates: false, canSilentlyDownload: true));
        };
        await using var service = CreateService(isPackaged: true, provider);
        List<AppUpdateState> states = new();
        service.SnapshotChanged += (_, snapshot) => states.Add(snapshot.State);

        await service.RunOnceAsync();

        Assert.Contains(AppUpdateState.Failed, states);
        Assert.Equal(AppUpdateState.Failed, service.CurrentSnapshot.State);
    }

    [Fact]
    public async Task RunOnceAsync_DownloadFailure_UsesFallbackStoreUi()
    {
        var provider = new FakeMicrosoftStoreUpdateProvider
        {
            DownloadResult = new StoreUpdateOperationResult(StoreUpdateOperationState.OtherError, FailedPackageCount: 1),
        };
        await using var service = CreateService(isPackaged: true, provider);

        await service.RunOnceAsync();

        Assert.Equal(AppUpdateState.Idle, service.CurrentSnapshot.State);
        Assert.Equal(1, provider.DownloadCount);
        Assert.Equal(1, provider.StoreUiCount);
    }

    [Fact]
    public async Task RunOnceAsync_DefersSilentInstallWhenAppIsBusy()
    {
        var provider = new FakeMicrosoftStoreUpdateProvider();
        await using var service = CreateService(isPackaged: true, provider, () => true);

        await service.RunOnceAsync();

        Assert.Equal(AppUpdateState.Idle, service.CurrentSnapshot.State);
        Assert.Equal(0, provider.InstallCount);
        Assert.Equal(0, provider.StoreUiCount);
    }

    [Fact]
    public async Task RunOnceAsync_ProgressCallbacksClampProgress()
    {
        var provider = new FakeMicrosoftStoreUpdateProvider
        {
            DownloadProgressValues = new[] { double.NaN, 1.5 },
        };
        await using var service = CreateService(isPackaged: true, provider);
        List<double> progressValues = new();
        service.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.IsProgressVisible)
            {
                progressValues.Add(snapshot.ProgressValue);
            }
        };

        await service.RunOnceAsync();

        Assert.All(progressValues, value => Assert.InRange(value, 0, 1));
        Assert.Contains(1, progressValues);
    }

    [Fact]
    public void FormatVersion_UsesFourPartsOnlyWhenRevisionIsPositive()
    {
        Assert.Equal("1.2.3.4", AppVersionProvider.FormatVersion(new Version(1, 2, 3, 4)));
        Assert.Equal("1.2.3", AppVersionProvider.FormatVersion(new Version(1, 2, 3, 0)));
        Assert.Equal("1.2", AppVersionProvider.FormatVersion(new Version(1, 2)));
    }

    [Fact]
    public async Task RunOnceAsync_LogsCoreMetadataSchema()
    {
        string logPath = Path.Combine(Path.GetTempPath(), $"AudioScript-update-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logPath);
        try
        {
            var provider = new FakeMicrosoftStoreUpdateProvider
            {
                QueryHandler = _ => Task.FromResult(QueryResult(hasUpdates: true, canSilentlyDownload: false)),
            };
            await using var service = new AppUpdateService(
                new FakeVersionProvider(isPackaged: true),
                provider,
                new InMemoryDeferredUpdateStateStore(),
                new ProcessLogService(logPath),
                () => false,
                new StoreUpdateOptions
                {
                    StartupDelay = TimeSpan.Zero,
                });

            await service.RunOnceAsync();

            string logFile = Directory.GetFiles(logPath, "audioscript-*.log").Single();
            string logText = await File.ReadAllTextAsync(logFile);
            Assert.Contains("operation=", logText, StringComparison.Ordinal);
            Assert.Contains("state=", logText, StringComparison.Ordinal);
            Assert.Contains("installedVersion=", logText, StringComparison.Ordinal);
            Assert.Contains("availableVersion=", logText, StringComparison.Ordinal);
            Assert.Contains("updateCount=", logText, StringComparison.Ordinal);
            Assert.Contains("mandatory=", logText, StringComparison.Ordinal);
            Assert.Contains("failedPackageCount=", logText, StringComparison.Ordinal);
            Assert.Contains("exceptionType=", logText, StringComparison.Ordinal);
            Assert.Contains("exceptionMessage=", logText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(logPath))
            {
                Directory.Delete(logPath, recursive: true);
            }
        }
    }

    private static AppUpdateService CreateService(
        bool isPackaged,
        FakeMicrosoftStoreUpdateProvider provider,
        Func<bool>? isBusy = null) =>
        new(
            new FakeVersionProvider(isPackaged),
            provider,
            new InMemoryDeferredUpdateStateStore(),
            new ProcessLogService(Path.Combine(Path.GetTempPath(), $"AudioScript-update-tests-{Guid.NewGuid():N}")),
            isBusy ?? (() => false),
            new StoreUpdateOptions
            {
                StartupDelay = TimeSpan.Zero,
            });

    private static StoreUpdateQueryResult QueryResult(bool hasUpdates, bool canSilentlyDownload)
    {
        StorePackageUpdateInfo[] updates = hasUpdates
            ? new[] { new StorePackageUpdateInfo("AudioScript_test", "2.0.0.0", IsMandatory: false) }
            : Array.Empty<StorePackageUpdateInfo>();
        return new StoreUpdateQueryResult(new StorePackageUpdateSet(updates), canSilentlyDownload);
    }

    private sealed class FakeVersionProvider : IAppVersionProvider
    {
        public FakeVersionProvider(bool isPackaged)
        {
            IsPackaged = isPackaged;
        }

        public bool IsPackaged { get; }

        public string InstalledVersion => "1.2.3.4";
    }

    private sealed class FakeMicrosoftStoreUpdateProvider : IMicrosoftStoreUpdateProvider
    {
        private readonly bool _isSupported;

        public FakeMicrosoftStoreUpdateProvider(bool isSupported = true)
        {
            _isSupported = isSupported;
        }

        public int QueryCount { get; private set; }

        public int DownloadCount { get; private set; }

        public int InstallCount { get; private set; }

        public int StoreUiCount { get; private set; }

        public Func<CancellationToken, Task<StoreUpdateQueryResult>> QueryHandler { get; set; } =
            _ => Task.FromResult(QueryResult(hasUpdates: true, canSilentlyDownload: true));

        public Func<StorePackageUpdateSet, Action<StoreUpdateOperationProgress>?, CancellationToken, Task<StoreUpdateOperationResult>>? DownloadHandler { get; set; }

        public Func<StorePackageUpdateSet, Action<StoreUpdateOperationProgress>?, CancellationToken, Task<StoreUpdateOperationResult>>? InstallHandler { get; set; }

        public Func<StorePackageUpdateSet, Action<StoreUpdateOperationProgress>?, CancellationToken, Task<StoreUpdateOperationResult>>? StoreUiHandler { get; set; }

        public StoreUpdateOperationResult DownloadResult { get; set; } =
            new(StoreUpdateOperationState.Completed);

        public StoreUpdateOperationResult InstallResult { get; set; } =
            new(StoreUpdateOperationState.Completed);

        public StoreUpdateOperationResult StoreUiResult { get; set; } =
            new(StoreUpdateOperationState.Completed);

        public IReadOnlyList<double> DownloadProgressValues { get; set; } = new[] { 1.0 };

        public IReadOnlyList<double> InstallProgressValues { get; set; } = new[] { 1.0 };

        public bool IsStoreUpdateSupported() => _isSupported;

        public Task<StoreUpdateQueryResult> GetAvailableUpdatesAsync(CancellationToken cancellationToken = default)
        {
            QueryCount++;
            return QueryHandler(cancellationToken);
        }

        public bool CanSilentlyDownloadUpdates(StoreUpdateQueryResult queryResult) =>
            queryResult.CanSilentlyDownload;

        public Task<StoreUpdateOperationResult> TrySilentDownloadAsync(
            StorePackageUpdateSet updateSet,
            Action<StoreUpdateOperationProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            foreach (double value in DownloadProgressValues)
            {
                progress?.Invoke(new StoreUpdateOperationProgress(value));
            }

            return DownloadHandler?.Invoke(updateSet, progress, cancellationToken)
                ?? Task.FromResult(DownloadResult);
        }

        public Task<StoreUpdateOperationResult> TrySilentDownloadAndInstallAsync(
            StorePackageUpdateSet updateSet,
            Action<StoreUpdateOperationProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            InstallCount++;
            foreach (double value in InstallProgressValues)
            {
                progress?.Invoke(new StoreUpdateOperationProgress(value));
            }

            return InstallHandler?.Invoke(updateSet, progress, cancellationToken)
                ?? Task.FromResult(InstallResult);
        }

        public Task<StoreUpdateOperationResult> RequestDownloadAndInstallWithStoreUiAsync(
            StorePackageUpdateSet updateSet,
            Action<StoreUpdateOperationProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            StoreUiCount++;
            foreach (double value in InstallProgressValues)
            {
                progress?.Invoke(new StoreUpdateOperationProgress(value));
            }

            return StoreUiHandler?.Invoke(updateSet, progress, cancellationToken)
                ?? Task.FromResult(StoreUiResult);
        }
    }

    private sealed class InMemoryDeferredUpdateStateStore : IDeferredUpdateStateStore
    {
        private DeferredUpdateState? _state;

        public Task<DeferredUpdateState?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_state);

        public Task SaveAsync(DeferredUpdateState state, CancellationToken cancellationToken = default)
        {
            _state = state;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            _state = null;
            return Task.CompletedTask;
        }
    }
}
