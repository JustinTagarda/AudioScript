namespace VoxTranscribe.Services;

public enum AppThemePreference {
    System,
    Light,
    Dark,
}

public sealed record AppThemeOption(
    AppThemePreference Preference,
    string DisplayName);

public sealed class AppThemeService {
    public static IReadOnlyList<AppThemeOption> ThemeOptions { get; } = new[] {
        new AppThemeOption(AppThemePreference.System, "Theme: System"),
        new AppThemeOption(AppThemePreference.Light, "Theme: Light"),
        new AppThemeOption(AppThemePreference.Dark, "Theme: Dark"),
    };

    public static string GetDisplayName(AppThemePreference preference) =>
        ThemeOptions.First(option => option.Preference == preference).DisplayName;

#pragma warning disable WPF0001
    public void Apply(AppThemePreference preference) {
        if (System.Windows.Application.Current is null) {
            return;
        }

        System.Windows.Application.Current.ThemeMode = preference switch {
            AppThemePreference.Light => System.Windows.ThemeMode.Light,
            AppThemePreference.Dark => System.Windows.ThemeMode.Dark,
            _ => System.Windows.ThemeMode.System,
        };
    }
#pragma warning restore WPF0001
}
