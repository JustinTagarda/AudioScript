using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using AudioScript.Services;

namespace AudioScript.ViewModels;

public sealed class StartupProvisioningWindowViewModel : INotifyPropertyChanged
{
    private string _headerText = "Initializing... please wait";
    private string _currentAssetText = "Checking required startup assets...";
    private string _currentActivityText = "Preparing startup asset installation.";
    private bool _showCancelButton = true;
    private bool _showCloseButton;
    private bool _wasSuccessful;

    public StartupProvisioningWindowViewModel(IEnumerable<ProvisionedAssetDescriptor> assets)
    {
        Assets = new ObservableCollection<StartupProvisioningAssetViewModel>(
            assets.Select(asset => new StartupProvisioningAssetViewModel(asset.Id, asset.DisplayName)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<StartupProvisioningAssetViewModel> Assets { get; }

    public string HeaderText
    {
        get => _headerText;
        private set => SetField(ref _headerText, value);
    }

    public string CurrentAssetText
    {
        get => _currentAssetText;
        private set => SetField(ref _currentAssetText, value);
    }

    public string CurrentActivityText
    {
        get => _currentActivityText;
        private set => SetField(ref _currentActivityText, value);
    }

    public bool IsBusy => !WasSuccessful && !ShowCloseButton;

    public bool ShowCancelButton
    {
        get => _showCancelButton;
        private set
        {
            if (!SetField(ref _showCancelButton, value))
            {
                return;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
        }
    }

    public bool ShowCloseButton
    {
        get => _showCloseButton;
        private set
        {
            if (!SetField(ref _showCloseButton, value))
            {
                return;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
        }
    }

    public bool WasSuccessful
    {
        get => _wasSuccessful;
        private set
        {
            if (_wasSuccessful == value)
            {
                return;
            }

            _wasSuccessful = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WasSuccessful)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
        }
    }

    public void UpdateProgress(AssetProvisioningProgress progress)
    {
        SetAssetStatus(progress.AssetId, progress.Status, progress.Percent);

        CurrentAssetText = progress.DisplayName;
        CurrentActivityText = $"{progress.DisplayName} is {progress.Status.TrimEnd('.').ToLowerInvariant()}";
        ShowCancelButton = true;
        ShowCloseButton = false;
        WasSuccessful = false;
    }

    public void SetAssetStatus(string assetId, string statusText, double percent)
    {
        if (Assets.FirstOrDefault(asset => string.Equals(asset.AssetId, assetId, StringComparison.OrdinalIgnoreCase)) is { } asset)
        {
            asset.Update(statusText, percent);
        }
    }

    public void MarkCompleted()
    {
        HeaderText = "Initialization complete.";
        CurrentAssetText = "Initialization complete.";
        CurrentActivityText = "Review the installed asset results, then click Close to continue.";
        ShowCancelButton = false;
        ShowCloseButton = true;
        WasSuccessful = true;

        foreach (StartupProvisioningAssetViewModel asset in Assets)
        {
            asset.MarkReady();
        }
    }

    public void MarkFailed(string message)
    {
        HeaderText = "Startup asset installation failed.";
        CurrentAssetText = "Startup asset installation failed.";
        CurrentActivityText = message;
        ShowCancelButton = false;
        ShowCloseButton = true;
        WasSuccessful = false;
    }

    public void MarkCanceled()
    {
        HeaderText = "Startup asset installation canceled.";
        CurrentAssetText = "Startup provisioning canceled.";
        CurrentActivityText = "The application will now exit.";
        ShowCancelButton = false;
        ShowCloseButton = true;
        WasSuccessful = false;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class StartupProvisioningAssetViewModel : INotifyPropertyChanged
{
    private string _statusText = "Waiting...";
    private double _percent;

    public StartupProvisioningAssetViewModel(string assetId, string displayName)
    {
        AssetId = assetId;
        DisplayName = displayName;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string AssetId { get; }

    public string DisplayName { get; }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        }
    }

    public string PercentText => $"{Math.Round(_percent):0}%";

    public double Percent
    {
        get => _percent;
        private set
        {
            if (Math.Abs(_percent - value) < 0.001)
            {
                return;
            }

            _percent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Percent)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PercentText)));
        }
    }

    public void Update(string statusText, double percent)
    {
        StatusText = statusText;
        Percent = Math.Clamp(percent, 0, 100);
    }

    public void MarkReady()
    {
        Update("Ready", 100);
    }
}
