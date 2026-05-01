using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using AudioScript.Services;
using AudioScript.ViewModels;

namespace AudioScript;

public partial class EngineModelSetupWindow : Window
{
    private readonly WhisperModelManager _modelManager;
    private readonly ProcessLogService _processLogService;
    private CancellationTokenSource? _activeInstallCts;
    private bool _closeAfterCancel;
    private bool _allowClose;

    public EngineModelSetupWindow(
        WhisperModelManager modelManager,
        ProcessLogService processLogService)
    {
        _modelManager = modelManager;
        _processLogService = processLogService;
        Items = new ObservableCollection<EngineModelSetupItemViewModel>(
            _modelManager.Models.Select(model =>
                new EngineModelSetupItemViewModel(model, _modelManager.IsModelInstalled(model.Id))));

        InitializeComponent();
        ModelsItemsControl.ItemsSource = Items;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
    }

    public ObservableCollection<EngineModelSetupItemViewModel> Items { get; }

    public bool HasModelChanges { get; private set; }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EngineModelSetupItemViewModel item)
        {
            return;
        }

        _activeInstallCts = new CancellationTokenSource();
        SetOperationState(item, isBusy: true);

        var progress = new Progress<WhisperModelInstallProgress>(item.ApplyProgress);
        try
        {
            await _modelManager.InstallModelAsync(item.Id, progress, _activeInstallCts.Token);
            HasModelChanges = true;
            RefreshInstalledStates();
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
        if ((sender as FrameworkElement)?.DataContext is not EngineModelSetupItemViewModel item)
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
        _activeInstallCts?.Dispose();
        _activeInstallCts = null;
    }

    private void SetOperationState(EngineModelSetupItemViewModel activeItem, bool isBusy)
    {
        activeItem.IsBusy = isBusy;
        foreach (EngineModelSetupItemViewModel item in Items)
        {
            item.IsOperationBlocked = isBusy && !ReferenceEquals(item, activeItem);
        }
    }

    private void RefreshInstalledStates()
    {
        foreach (EngineModelSetupItemViewModel item in Items)
        {
            item.RefreshInstalled(_modelManager.IsModelInstalled(item.Id));
        }
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
