using AudioScript.Services;
using AudioScript.Services.Store;
using AudioScript.ViewModels;
using Xunit;

namespace AudioScript.Tests;

public sealed class AppStatusViewModelTests
{
    [Fact]
    public async Task CheckForUpdatesCommand_UsesUpdateCoordinator()
    {
        var updateService = new StubAppUpdateService();
        var viewModel = new AppStatusViewModel(
            new StubLicenseService(),
            new StubPurchaseService(),
            new StubNavigationService(),
            new AppVersionService(new StubVersionProvider()),
            updateService);

        Assert.True(viewModel.CanCheckForUpdates);
        viewModel.CheckForUpdatesCommand.Execute(null);
        await Task.Delay(100);

        Assert.Equal(1, updateService.UserInitiatedUpdateFlowCallCount);
        Assert.True(viewModel.CanCheckForUpdates);
    }

    [Fact]
    public async Task RestorePurchaseCommand_NoPurchase_ShowsRestoreToast()
    {
        var viewModel = new AppStatusViewModel(
            new StubLicenseService(),
            new StubPurchaseService(),
            new StubNavigationService(),
            new AppVersionService(new StubVersionProvider()));

        viewModel.RestorePurchaseCommand.Execute(null);
        await Task.Delay(100);

        Assert.Equal("No Premium purchase found", viewModel.VersionToastText);
        Assert.True(viewModel.IsVersionToastVisible);
    }

    private sealed class StubLicenseService : IStoreLicenseService
    {
        public AppEntitlementSnapshot CurrentSnapshot => new(
            IsPackaged: true,
            HasPremium: false,
            IsPremiumProductAvailable: true,
            PremiumProductDisplayName: "AudioScript Premium",
            StatusMessage: "Basic mode.");

        public event EventHandler<AppEntitlementSnapshot>? SnapshotChanged;

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubPurchaseService : IStorePurchaseService
    {
        public Task<PremiumPurchaseResult> RequestPremiumPurchaseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PremiumPurchaseResult(PremiumPurchaseStatus.NotAvailable, "Not available."));
    }

    private sealed class StubNavigationService : IStoreNavigationService
    {
        public bool CanOpenAppStorePage => true;

        public Task OpenAppStorePageAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubAppUpdateService : IAppUpdateService
    {
        public bool IsStoreUpdateSupported => true;

        public AppUpdateSnapshot CurrentSnapshot => AppUpdateSnapshot.Idle("1.2.3.4");

        public event EventHandler<AppUpdateSnapshot>? SnapshotChanged;

        public int UserInitiatedUpdateFlowCallCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RunUserInitiatedUpdateFlowAsync(CancellationToken cancellationToken = default)
        {
            UserInitiatedUpdateFlowCallCount++;
            return Task.CompletedTask;
        }

        public Task<StoreUpdateOperationResult?> RunExitTimeInstallAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<StoreUpdateOperationResult?>(null);

        public Task<bool> HasDeferredInstallOnExitAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task StopAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubVersionProvider : IAppVersionProvider
    {
        public bool IsPackaged => true;

        public string InstalledVersion => "1.2.3.4";
    }
}
