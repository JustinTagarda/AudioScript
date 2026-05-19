using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class AppUpdateServiceTests
{
    [Fact]
    public async Task RunOnceAsync_Unpackaged_SkipsStoreOperations()
    {
        var client = new FakeStoreUpdateClient();
        await using var service = CreateService(isPackaged: false, client);

        await service.RunOnceAsync();

        Assert.Equal(AppUpdateState.Idle, service.CurrentSnapshot.State);
        Assert.Equal(0, client.QueryCount);
    }

    [Fact]
    public async Task RunOnceAsync_NoUpdates_ReturnsIdle()
    {
        var client = new FakeStoreUpdateClient
        {
            QueryHandler = _ => Task.FromResult(QueryResult(hasUpdates: false, canSilentlyDownload: true)),
        };
        await using var service = CreateService(isPackaged: true, client);

        await service.RunOnceAsync();

        Assert.Equal(AppUpdateState.Idle, service.CurrentSnapshot.State);
        Assert.Equal("1.2.3.4", service.CurrentSnapshot.InstalledVersion);
        Assert.Equal(1, client.QueryCount);
    }

    [Fact]
    public async Task RunOnceAsync_UpdateAvailableWithoutSilentDownload_DefersAndRetriesDiscovery()
    {
        var client = new FakeStoreUpdateClient
        {
            QueryHandler = _ => Task.FromResult(QueryResult(hasUpdates: true, canSilentlyDownload: false)),
        };
        await using var service = CreateService(isPackaged: true, client);

        await service.RunOnceAsync();

        Assert.Equal(AppUpdateState.Deferred, service.CurrentSnapshot.State);
        Assert.Equal(0, client.DownloadCount);
        await WaitUntilAsync(() => client.QueryCount >= 2);
    }

    [Fact]
    public async Task RunOnceAsync_DownloadsAndPublishesRestartRequiredWithoutInstall()
    {
        bool isBusy = true;
        var client = new FakeStoreUpdateClient();
        await using var service = CreateService(isPackaged: true, client, () => isBusy);
        List<AppUpdateState> states = new();
        service.SnapshotChanged += (_, snapshot) => states.Add(snapshot.State);

        await service.RunOnceAsync();

        Assert.Contains(AppUpdateState.Downloading, states);
        Assert.Equal(1, client.DownloadCount);
        Assert.Equal(0, client.InstallCount);
        Assert.Equal(AppUpdateState.Completed, service.CurrentSnapshot.State);
    }

    [Fact]
    public async Task RunOnceAsync_CheckFailure_PublishesFailedAndRetries()
    {
        var client = new FakeStoreUpdateClient();
        client.QueryHandler = _ =>
        {
            if (client.QueryCount == 1)
            {
                throw new InvalidOperationException("query failed");
            }

            return Task.FromResult(QueryResult(hasUpdates: false, canSilentlyDownload: true));
        };
        await using var service = CreateService(isPackaged: true, client);
        List<AppUpdateState> states = new();
        service.SnapshotChanged += (_, snapshot) => states.Add(snapshot.State);

        await service.RunOnceAsync();

        Assert.Contains(AppUpdateState.Failed, states);
        await WaitUntilAsync(() => client.QueryCount >= 2);
        Assert.Equal(AppUpdateState.Idle, service.CurrentSnapshot.State);
    }

    [Fact]
    public async Task RunOnceAsync_DownloadFailure_DefersAndRetriesDiscovery()
    {
        var client = new FakeStoreUpdateClient
        {
            DownloadResult = new StoreUpdateOperationResult(StoreUpdateOperationState.OtherError, FailedPackageCount: 1),
        };
        await using var service = CreateService(isPackaged: true, client);

        await service.RunOnceAsync();

        Assert.Equal(AppUpdateState.Deferred, service.CurrentSnapshot.State);
        Assert.Equal(1, client.DownloadCount);
        await WaitUntilAsync(() => client.QueryCount >= 2);
    }

    [Fact]
    public async Task RunOnceAsync_DoesNotCallInstallDuringForegroundFlow()
    {
        var client = new FakeStoreUpdateClient();
        await using var service = CreateService(isPackaged: true, client);

        await service.RunOnceAsync();

        Assert.Equal(AppUpdateState.Completed, service.CurrentSnapshot.State);
        Assert.Equal(0, client.InstallCount);
    }

    [Fact]
    public async Task RunOnceAsync_ProgressCallbacksClampProgress()
    {
        var client = new FakeStoreUpdateClient
        {
            DownloadProgressValues = new[] { double.NaN, 1.5 },
        };
        await using var service = CreateService(isPackaged: true, client);
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
            var client = new FakeStoreUpdateClient
            {
                QueryHandler = _ => Task.FromResult(QueryResult(hasUpdates: true, canSilentlyDownload: false)),
            };
            await using var service = new AppUpdateService(
                new FakeVersionProvider(isPackaged: true),
                client,
                new ProcessLogService(logPath),
                () => false,
                new AppUpdateServiceOptions
                {
                    StartupDelay = TimeSpan.Zero,
                    DiscoveryRetryDelay = TimeSpan.FromMinutes(10),
                    InstallRetryDelay = TimeSpan.FromMilliseconds(20),
                    InstallQuietPeriod = TimeSpan.FromMilliseconds(1),
                    MaxDiscoveryRetryCount = 0,
                    MaxInstallRetryCount = 0,
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
        FakeStoreUpdateClient client,
        Func<bool>? isBusy = null) =>
        new(
            new FakeVersionProvider(isPackaged),
            client,
            new ProcessLogService(Path.Combine(Path.GetTempPath(), $"AudioScript-update-tests-{Guid.NewGuid():N}")),
            isBusy ?? (() => false),
            new AppUpdateServiceOptions
            {
                StartupDelay = TimeSpan.Zero,
                DiscoveryRetryDelay = TimeSpan.FromMilliseconds(20),
                InstallRetryDelay = TimeSpan.FromMilliseconds(20),
                InstallQuietPeriod = TimeSpan.FromMilliseconds(1),
                MaxDiscoveryRetryCount = 1,
                MaxInstallRetryCount = 1,
            });

    private static StoreUpdateQueryResult QueryResult(bool hasUpdates, bool canSilentlyDownload)
    {
        StorePackageUpdateInfo[] updates = hasUpdates
            ? new[] { new StorePackageUpdateInfo("AudioScript_test", "2.0.0.0", IsMandatory: false) }
            : Array.Empty<StorePackageUpdateInfo>();
        return new StoreUpdateQueryResult(new StorePackageUpdateSet(updates), canSilentlyDownload);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate())
        {
            await Task.Delay(10, timeoutCts.Token);
        }
    }

    private sealed class FakeVersionProvider : IAppVersionProvider
    {
        public FakeVersionProvider(bool isPackaged)
        {
            IsPackaged = isPackaged;
        }

        public bool IsPackaged { get; }

        public string InstalledVersion => "1.2.3.4";

        public string DisplayVersionText => "Version 1.2.3.4";
    }

    private sealed class FakeStoreUpdateClient : IStoreUpdateClient
    {
        public int QueryCount { get; private set; }

        public int DownloadCount { get; private set; }

        public int InstallCount { get; private set; }

        public Func<CancellationToken, Task<StoreUpdateQueryResult>> QueryHandler { get; set; } =
            _ => Task.FromResult(QueryResult(hasUpdates: true, canSilentlyDownload: true));

        public Func<StorePackageUpdateSet, Action<StoreUpdateOperationProgress>?, CancellationToken, Task<StoreUpdateOperationResult>>? DownloadHandler { get; set; }

        public Func<StorePackageUpdateSet, Action<StoreUpdateOperationProgress>?, CancellationToken, Task<StoreUpdateOperationResult>>? InstallHandler { get; set; }

        public StoreUpdateOperationResult DownloadResult { get; set; } =
            new(StoreUpdateOperationState.Completed);

        public StoreUpdateOperationResult InstallResult { get; set; } =
            new(StoreUpdateOperationState.Completed);

        public IReadOnlyList<double> DownloadProgressValues { get; set; } = new[] { 1.0 };

        public IReadOnlyList<double> InstallProgressValues { get; set; } = new[] { 1.0 };

        public Task<StoreUpdateQueryResult> QueryUpdatesAsync(CancellationToken cancellationToken)
        {
            QueryCount++;
            return QueryHandler(cancellationToken);
        }

        public Task<StoreUpdateOperationResult> DownloadUpdatesAsync(
            StorePackageUpdateSet updateSet,
            Action<StoreUpdateOperationProgress>? progress,
            CancellationToken cancellationToken)
        {
            DownloadCount++;
            foreach (double value in DownloadProgressValues)
            {
                progress?.Invoke(new StoreUpdateOperationProgress(value));
            }

            return DownloadHandler?.Invoke(updateSet, progress, cancellationToken)
                ?? Task.FromResult(DownloadResult);
        }

        public Task<StoreUpdateOperationResult> InstallUpdatesAsync(
            StorePackageUpdateSet updateSet,
            Action<StoreUpdateOperationProgress>? progress,
            CancellationToken cancellationToken)
        {
            InstallCount++;
            foreach (double value in InstallProgressValues)
            {
                progress?.Invoke(new StoreUpdateOperationProgress(value));
            }

            return InstallHandler?.Invoke(updateSet, progress, cancellationToken)
                ?? Task.FromResult(InstallResult);
        }
    }
}
