using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioScript.Services;

namespace AudioScript.ViewModels;

public sealed class SettingsItemViewModel : INotifyPropertyChanged
{
    private bool _isInstalled;
    private bool _hasPremiumAccess;
    private bool _isBusy;
    private bool _isOperationBlocked;
    private double _progressPercent;
    private string _progressText = string.Empty;

    public SettingsItemViewModel(WhisperEngineModelDefinition definition, bool isInstalled, bool hasPremiumAccess)
    {
        Definition = definition;
        _isInstalled = isInstalled;
        _hasPremiumAccess = hasPremiumAccess;
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

    public bool IsPremiumOnlyEngine => AppFeatureAccess.IsPremiumOnlyModel(Id);

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
            : RequiresPremiumToInstall
                ? "Premium required"
            : "Not installed";

    public bool CanInstall =>
        !RequiresPremiumToInstall && !IsInstalled && !IsFixedInstalled && !IsBusy && !IsOperationBlocked;

    public bool CanUninstall => IsInstalled && !IsFixedInstalled && !IsBusy && !IsOperationBlocked;

    public bool CanCancel => IsBusy;

    public bool ShowInstallButton => !IsInstalled && !IsBusy;

    public bool ShowFixedInstalledNotice => IsFixedInstalled;

    public bool RequiresPremiumToInstall => IsPremiumOnlyEngine && !_hasPremiumAccess;

    public bool ShowPremiumUpsell => RequiresPremiumToInstall && !IsBusy;

    public string PremiumUpsellText => "Premium required to install this engine.";

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

    public void SetPremiumAccess(bool hasPremiumAccess)
    {
        if (SetProperty(ref _hasPremiumAccess, hasPremiumAccess))
        {
            NotifyActionPropertiesChanged();
            OnPropertyChanged(nameof(RequiresPremiumToInstall));
            OnPropertyChanged(nameof(ShowPremiumUpsell));
        }
    }

    private void NotifyActionPropertiesChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanUninstall));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(ShowInstallButton));
        OnPropertyChanged(nameof(ShowFixedInstalledNotice));
        OnPropertyChanged(nameof(RequiresPremiumToInstall));
        OnPropertyChanged(nameof(ShowPremiumUpsell));
        OnPropertyChanged(nameof(PremiumUpsellText));
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
