using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioScript.Services;
using AudioScript.Services.Store;

namespace AudioScript.ViewModels;

public sealed class AppStatusViewModel : INotifyPropertyChanged
{
    private readonly IStoreLicenseService _licenseService;
    private readonly IStorePurchaseService _purchaseService;
    private readonly IStoreNavigationService _navigationService;
    private readonly IAppUpdateService? _appUpdateService;
    private readonly IAppVersionService _versionService;
    private readonly SynchronizationContext _uiContext;
    private AppEntitlementSnapshot _entitlementSnapshot;
    private string _versionToastText = string.Empty;
    private bool _isVersionToastVisible;
    private CancellationTokenSource? _versionToastCts;

    public AppStatusViewModel(
        IStoreLicenseService licenseService,
        IStorePurchaseService purchaseService,
        IStoreNavigationService navigationService,
        IAppVersionService versionService,
        IAppUpdateService? appUpdateService = null)
    {
        _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));
        _purchaseService = purchaseService ?? throw new ArgumentNullException(nameof(purchaseService));
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _versionService = versionService ?? throw new ArgumentNullException(nameof(versionService));
        _appUpdateService = appUpdateService;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _entitlementSnapshot = _licenseService.CurrentSnapshot;
        _licenseService.SnapshotChanged += OnLicenseSnapshotChanged;
        if (_appUpdateService is not null)
        {
            _appUpdateService.SnapshotChanged += OnAppUpdateSnapshotChanged;
        }

        UpgradeCommand = new AsyncRelayCommand(UpgradeAsync, CanUpgrade);
        RestorePurchaseCommand = new AsyncRelayCommand(() => RestorePurchaseAsync());
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, () => CanCheckForUpdates);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AsyncRelayCommand UpgradeCommand { get; }

    public AsyncRelayCommand RestorePurchaseCommand { get; }

    public AsyncRelayCommand CheckForUpdatesCommand { get; }

    public string ModeText => _entitlementSnapshot.HasPremium ? "Premium" : "Basic";

    public string VersionText => _versionService.VersionText;

    public bool IsPremium => _entitlementSnapshot.HasPremium;

    public bool CanCheckForUpdates =>
        _appUpdateService?.IsStoreUpdateSupported == true
        && _appUpdateService.CurrentSnapshot.State is not AppUpdateState.Checking
            and not AppUpdateState.UpdateAvailable
            and not AppUpdateState.Downloading
            and not AppUpdateState.Installing;

    public string ModeTooltip => IsPremium
        ? "Premium mode active."
        : "Click to upgrade to Premium.";

    public string VersionToastText
    {
        get => _versionToastText;
        private set => SetProperty(ref _versionToastText, value);
    }

    public bool IsVersionToastVisible
    {
        get => _isVersionToastVisible;
        private set => SetProperty(ref _isVersionToastVisible, value);
    }

    public async Task RestorePurchaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _licenseService.RefreshAsync(cancellationToken).ConfigureAwait(false);
            ShowVersionToast(_licenseService.CurrentSnapshot.HasPremium
                ? "Premium restored"
                : "No Premium purchase found");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            ShowVersionToast("Unable to verify purchase right now");
        }
    }

    private bool CanUpgrade() => !_entitlementSnapshot.HasPremium;

    private async Task CheckForUpdatesAsync()
    {
        if (_appUpdateService is null || !_appUpdateService.IsStoreUpdateSupported)
        {
            ShowVersionToast("Update check unavailable.");
            return;
        }

        try
        {
            await _appUpdateService.RunUserInitiatedUpdateFlowAsync().ConfigureAwait(false);
        }
        catch
        {
            ShowVersionToast("Update check unavailable.");
        }
    }

    private async Task UpgradeAsync()
    {
        if (_entitlementSnapshot.HasPremium)
        {
            return;
        }

        PremiumPurchaseResult result = await _purchaseService.RequestPremiumPurchaseAsync().ConfigureAwait(false);
        if (result.Status == PremiumPurchaseStatus.NotAvailable
            && !_versionService.IsPackaged
            && _navigationService.CanOpenAppStorePage)
        {
            try
            {
                await _navigationService.OpenAppStorePageAsync().ConfigureAwait(false);
                return;
            }
            catch
            {
                ShowVersionToast("Premium purchase unavailable in this build.");
                return;
            }
        }

        ShowVersionToast(result.Status == PremiumPurchaseStatus.NotAvailable && !_navigationService.CanOpenAppStorePage
            ? "Premium purchase unavailable in this build."
            : result.Message);
    }

    private void ShowVersionToast(string message)
    {
        _uiContext.Post(_ =>
        {
            VersionToastText = message;
            IsVersionToastVisible = true;
        }, null);
        _versionToastCts?.Cancel();
        _versionToastCts?.Dispose();
        _versionToastCts = new CancellationTokenSource();
        CancellationToken token = _versionToastCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                _uiContext.Post(_ => IsVersionToastVisible = false, null);
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void HideVersionToast()
    {
        _versionToastCts?.Cancel();
        _versionToastCts?.Dispose();
        _versionToastCts = null;
        _uiContext.Post(_ => IsVersionToastVisible = false, null);
    }

    private void OnLicenseSnapshotChanged(object? sender, AppEntitlementSnapshot snapshot)
    {
        _entitlementSnapshot = snapshot;
        _uiContext.Post(_ =>
        {
            OnPropertyChanged(nameof(ModeText));
            OnPropertyChanged(nameof(IsPremium));
            OnPropertyChanged(nameof(ModeTooltip));
            UpgradeCommand.RaiseCanExecuteChanged();
        }, null);
    }

    private void OnAppUpdateSnapshotChanged(object? sender, AppUpdateSnapshot snapshot)
    {
        _uiContext.Post(_ =>
        {
            OnPropertyChanged(nameof(CanCheckForUpdates));
            CheckForUpdatesCommand.RaiseCanExecuteChanged();
        }, null);
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
