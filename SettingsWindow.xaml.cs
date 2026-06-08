using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using AudioScript.Services;
using AudioScript.Services.Store;
using AudioScript.ViewModels;

namespace AudioScript;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly WhisperModelManager _modelManager;
    private readonly ProcessLogService _processLogService;
    private CancellationTokenSource? _activeInstallCts;
    private bool _closeAfterCancel;
    private bool _allowClose;

    public SettingsWindow(
        MainViewModel viewModel,
        WhisperModelManager modelManager,
        ProcessLogService processLogService)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        _modelManager = modelManager;
        _processLogService = processLogService;
        Items = new ObservableCollection<SettingsItemViewModel>(
            _modelManager.Models
                .Where(model => !model.IsBundled)
                .Select(model =>
                new SettingsItemViewModel(
                    model,
                    _modelManager.IsModelInstalled(model.Id),
                    _viewModel.HasPremium,
                    _viewModel.IsDevelopmentUnpackagedMode)));

        InitializeComponent();
        ModelsItemsControl.ItemsSource = Items;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public ObservableCollection<SettingsItemViewModel> Items { get; }

    public bool HasModelChanges { get; private set; }

    public string? LastInstalledModelId { get; private set; }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SettingsItemViewModel item)
        {
            return;
        }

        if (!_viewModel.CanInstallModel(item.Id))
        {
            await RequestPremiumPurchaseAsync(item.DisplayName);
            return;
        }

        _activeInstallCts = new CancellationTokenSource();
        SetOperationState(item, isBusy: true);

        var progress = new Progress<WhisperModelInstallProgress>(item.ApplyProgress);
        try
        {
            await _modelManager.InstallModelAsync(item.Id, progress, _activeInstallCts.Token);
            HasModelChanges = true;
            LastInstalledModelId = item.Id;
            RefreshInstalledStates();
            _viewModel.RefreshEngines(_modelManager.GetSelectableTranscriptionModels(), item.Id);
            item.ClearProgress("Installed.");
        }
        catch (OperationCanceledException)
        {
            RefreshInstalledStates();
            item.ClearProgress("Installation canceled.");
        }
        catch (Exception ex)
        {
            _processLogService.LogException("EngineModels", $"Failed to install '{item.DisplayName}'.", ex);
            RefreshInstalledStates();
            item.ClearProgress("Installation failed.");
            ShowError($"Unable to install {item.DisplayName}: {ex.Message}");
        }
        finally
        {
            SetOperationState(item, isBusy: false);
            _activeInstallCts?.Dispose();
            _activeInstallCts = null;

            if (_closeAfterCancel)
            {
                _allowClose = true;
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
    }

    private void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SettingsItemViewModel item)
        {
            return;
        }

        string freedSizeText = FormatBytes(_modelManager.GetInstalledModelSize(item.Id));
        var confirmation = new ConfirmationDialogWindow(
            "Uninstall engine model",
            $"Uninstall {item.DisplayName}? This deletes the downloaded engine file and frees about {freedSizeText}. It will be removed from the engine selector until installed again.",
            "Uninstall",
            "Cancel")
        {
            Owner = this,
        };

        if (confirmation.ShowDialog() != true)
        {
            return;
        }

        try
        {
            WhisperModelUninstallResult result = _modelManager.UninstallModel(item.Id);
            HasModelChanges = true;
            RefreshInstalledStates();
            item.ClearProgress(result.WasDeleted
                ? $"Uninstalled. Freed {FormatBytes(result.DeletedBytes)}."
                : "Already removed.");
        }
        catch (Exception ex)
        {
            _processLogService.LogException("EngineModels", $"Failed to uninstall '{item.DisplayName}'.", ex);
            ShowError($"Unable to uninstall {item.DisplayName}: {ex.Message}");
        }
    }

    private void CancelInstallButton_Click(object sender, RoutedEventArgs e)
    {
        _activeInstallCts?.Cancel();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _activeInstallCts is null)
        {
            return;
        }

        var confirmation = new ConfirmationDialogWindow(
            "Cancel installation",
            "Closing this window will cancel the active model installation.",
            "Cancel install",
            "Keep open")
        {
            Owner = this,
        };

        if (confirmation.ShowDialog() == true)
        {
            e.Cancel = true;
            _closeAfterCancel = true;
            _activeInstallCts.Cancel();
            return;
        }

        e.Cancel = true;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        Closing -= OnWindowClosing;
        Closed -= OnWindowClosed;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _activeInstallCts?.Dispose();
        _activeInstallCts = null;
    }

    private async void GetPremiumButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SettingsItemViewModel item)
        {
            return;
        }

        await RequestPremiumPurchaseAsync(item.DisplayName);
    }

    private void SetOperationState(SettingsItemViewModel activeItem, bool isBusy)
    {
        activeItem.IsBusy = isBusy;
        foreach (SettingsItemViewModel item in Items)
        {
            item.IsOperationBlocked = isBusy && !ReferenceEquals(item, activeItem);
        }
    }

    private void RefreshInstalledStates()
    {
        foreach (SettingsItemViewModel item in Items)
        {
            item.RefreshInstalled(_modelManager.IsModelInstalled(item.Id));
        }
    }

    private void RefreshPremiumAccess()
    {
        foreach (SettingsItemViewModel item in Items)
        {
            item.SetPremiumAccess(_viewModel.HasPremium);
            item.SetDevelopmentUnpackagedMode(_viewModel.IsDevelopmentUnpackagedMode);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(MainViewModel.HasPremium), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(MainViewModel.IsDevelopmentUnpackagedMode), StringComparison.Ordinal))
        {
            return;
        }

        RefreshPremiumAccess();
    }

    private async Task RequestPremiumPurchaseAsync(string featureName)
    {
        try
        {
            if (_viewModel.IsDevelopmentUnpackagedMode)
            {
                ShowError(
                    $"Microsoft Store purchase for {_viewModel.PremiumProductDisplayName} is unavailable in local debug runs.");
                return;
            }

            if (_viewModel.IsPremiumEntitlementChecking || _viewModel.IsPremiumEntitlementVerificationFailed)
            {
                await _viewModel.RefreshPremiumEntitlementAsync();
                RefreshPremiumAccess();
                if (_viewModel.HasPremium)
                {
                    return;
                }
            }

            if (_viewModel.IsPremiumEntitlementChecking)
            {
                ShowError("AudioScript is still checking Microsoft Store entitlement. Please try again in a moment.");
                return;
            }

            if (_viewModel.IsPremiumEntitlementVerificationFailed)
            {
                ShowError("AudioScript could not verify Microsoft Store entitlement right now. Please ensure Microsoft Store is signed in, then click Restore.");
                return;
            }

            if (!_viewModel.CanPromptPremiumPurchase)
            {
                ShowError("Premium purchase is not available until entitlement verification completes.");
                return;
            }

            var confirmation = new ConfirmationDialogWindow(
                "Premium feature",
                $"{_viewModel.PremiumProductDisplayName} unlocks all premium features. Upgrade in Microsoft Store to continue.",
                "Get Premium",
                "Not now")
            {
                Owner = this,
            };

            if (confirmation.ShowDialog() != true)
            {
                return;
            }

            Window? initiatingWindow = GetInitiatingWindow();
            PremiumPurchaseResult result = null!;
            try
            {
                IntPtr initiatingWindowHandle = IntPtr.Zero;
                try
                {
                    initiatingWindowHandle = new WindowInteropHelper(initiatingWindow).Handle;
                }
                catch (Exception ex)
                {
                    _processLogService.LogException("Premium", $"Unable to resolve owner window handle for '{featureName}'.", ex);
                }

                using IDisposable scope = StorePurchaseOwnerWindowBinding.BeginScope(initiatingWindowHandle);
                result = await _viewModel.RequestPremiumPurchaseAsync();
            }
            finally
            {
                StoreWindowAccessibilityRecovery.RecoverAfterStoreFlow(
                    initiatingWindow ?? this,
                    System.Windows.Application.Current?.MainWindow,
                    (context, ex) => _processLogService.LogException("Premium", context, ex));
            }

            RefreshPremiumAccess();
            if (result.Status is PremiumPurchaseStatus.Succeeded or PremiumPurchaseStatus.AlreadyOwned
                || result.Status == PremiumPurchaseStatus.Canceled)
            {
                return;
            }

            ShowError(result.Message);
        }
        catch (Exception ex)
        {
            _processLogService.LogException("Premium", $"Unable to open Premium purchase flow for '{featureName}'.", ex);
            ShowError($"Unable to open Microsoft Store for {_viewModel.PremiumProductDisplayName}: {ex.Message}");
        }
    }

    private Window GetInitiatingWindow()
    {
        return System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
            ?? System.Windows.Application.Current?.MainWindow
            ?? this;
    }

    private void ShowError(string message)
    {
        var dialog = new ErrorDialogWindow(message)
        {
            Owner = this,
        };
        dialog.ShowDialog();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_000_000_000)
        {
            return $"{bytes / 1_000_000_000d:F2} GB";
        }

        if (bytes >= 1_000_000)
        {
            return $"{bytes / 1_000_000d:F1} MB";
        }

        if (bytes >= 1_000)
        {
            return $"{bytes / 1_000d:F1} KB";
        }

        return $"{bytes:N0} bytes";
    }
}
