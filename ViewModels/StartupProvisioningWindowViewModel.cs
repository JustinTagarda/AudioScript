using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using AudioScript.Services;

namespace AudioScript.ViewModels;

public sealed class StartupProvisioningWindowViewModel : INotifyPropertyChanged
{
    private string _headerText = "Initializing... please wait";
    private string _currentAssetText = "Checking required startup dependencies...";
    private string _currentActivityText = "Downloading and installing required startup dependencies.";
    private bool _showCancelButton = true;
    private bool _wasCanceled;

    public StartupProvisioningWindowViewModel(IEnumerable<ProvisionedAssetDescriptor> assets)
    {
        Assets = new ObservableCollection<StartupProvisioningAssetViewModel>(
            assets.Select(asset => new StartupProvisioningAssetViewModel(asset.Id, asset.DisplayName)));
    }

    public StartupProvisioningWindowViewModel(IEnumerable<(string Id, string DisplayName)> dependencies)
    {
        Assets = new ObservableCollection<StartupProvisioningAssetViewModel>(
            dependencies.Select(dependency => new StartupProvisioningAssetViewModel(dependency.Id, dependency.DisplayName)));
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

    public bool IsBusy => !WasCanceled;

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

    public void UpdateProgress(AssetProvisioningProgress progress)
    {
        SetAssetStatus(progress.AssetId, progress.Status, progress.Percent);

        CurrentAssetText = progress.DisplayName;
        CurrentActivityText = $"{progress.DisplayName} is {progress.Status.TrimEnd('.').ToLowerInvariant()}";
        ShowCancelButton = true;
        WasCanceled = false;
    }

    public void UpdateProgress(StartupDependencyHealthProgress progress)
    {
        string statusText = progress.Status.ToString();
        if (progress.Attempt > 0 && progress.MaxAttempts > 0)
        {
            statusText = $"{statusText} ({progress.Attempt}/{progress.MaxAttempts})";
        }

        SetAssetStatus(progress.DependencyId, statusText, progress.Percent);
        CurrentAssetText = progress.DisplayName;
        CurrentActivityText = progress.Message;
        ShowCancelButton = true;
        WasCanceled = false;
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
        CurrentActivityText = "All required startup dependencies are ready.";
        ShowCancelButton = false;
        WasCanceled = false;

        foreach (StartupProvisioningAssetViewModel asset in Assets)
        {
            asset.MarkReady();
        }
    }

    public void MarkFailed(string message)
    {
        HeaderText = "Startup dependency check completed with issues.";
        CurrentAssetText = "Startup dependency check completed with issues.";
        CurrentActivityText = message;
        ShowCancelButton = false;
        WasCanceled = false;
    }

    public void MarkCanceled()
    {
        HeaderText = "Startup asset installation canceled.";
        CurrentAssetText = "Startup provisioning canceled.";
        CurrentActivityText = "The application will now exit.";
        ShowCancelButton = false;
        WasCanceled = true;
    }

    public bool WasCanceled
    {
        get => _wasCanceled;
        private set
        {
            if (!SetField(ref _wasCanceled, value))
            {
                return;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
        }
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
