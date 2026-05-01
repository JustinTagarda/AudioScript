using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioScript.Services;

namespace AudioScript.ViewModels;

public sealed class EngineModelSetupItemViewModel : INotifyPropertyChanged
{
    private bool _isInstalled;
    private bool _isBusy;
    private bool _isOperationBlocked;
    private double _progressPercent;
    private string _progressText = string.Empty;

    public EngineModelSetupItemViewModel(WhisperEngineModelDefinition definition, bool isInstalled)
    {
        Definition = definition;
        _isInstalled = isInstalled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public WhisperEngineModelDefinition Definition { get; }

    public string Id => Definition.Id;

    public string DisplayName => Definition.DisplayName;

    public string SizeText => Definition.SizeText;

    public string Description => Definition.Description;

    public string Benefits => Definition.Benefits;

    public string Notes => Definition.Notes;

    public bool IsFixedInstalled => Definition.IsFixedInstalled;

    public bool IsInstalled
    {
        get => _isInstalled;
        private set
        {
            if (SetProperty(ref _isInstalled, value))
            {
                NotifyActionPropertiesChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyActionPropertiesChanged();
            }
        }
    }

    public bool IsOperationBlocked
    {
        get => _isOperationBlocked;
        set
        {
            if (SetProperty(ref _isOperationBlocked, value))
            {
                NotifyActionPropertiesChanged();
            }
        }
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public string StatusText => IsBusy
        ? "Installing"
        : IsInstalled
            ? "Installed"
            : "Not installed";

    public bool CanInstall => !IsInstalled && !IsFixedInstalled && !IsBusy && !IsOperationBlocked;

    public bool CanUninstall => IsInstalled && !IsFixedInstalled && !IsBusy && !IsOperationBlocked;

    public bool CanCancel => IsBusy;

    public bool ShowFixedInstalledNotice => IsFixedInstalled;

    public void RefreshInstalled(bool isInstalled)
    {
        IsInstalled = isInstalled;
        if (!IsBusy)
        {
            ProgressPercent = isInstalled ? 100 : 0;
            ProgressText = string.Empty;
        }
    }

    public void ApplyProgress(WhisperModelInstallProgress progress)
    {
        ProgressPercent = Math.Max(0, Math.Min(100, progress.Percent));
        string downloaded = FormatBytes(progress.BytesReceived);
        string total = progress.TotalBytes is > 0 ? FormatBytes(progress.TotalBytes.Value) : "unknown size";
        ProgressText = $"{progress.Status} {downloaded} / {total}";
    }

    public void ClearProgress(string message = "")
    {
        ProgressText = message;
        ProgressPercent = IsInstalled ? 100 : 0;
    }

    private void NotifyActionPropertiesChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanUninstall));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(ShowFixedInstalledNotice));
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
        if (!string.IsNullOrWhiteSpace(propertyName))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
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

        return $"{bytes:N0} bytes";
    }
}

