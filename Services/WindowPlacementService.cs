using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using Path = System.IO.Path;
using Directory = System.IO.Directory;
using File = System.IO.File;

namespace AudioTranscript.Services;

public sealed class WindowPlacementService {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
    };

    private readonly string _stateFilePath;

    public WindowPlacementService() {
        var appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioTranscript");
        _stateFilePath = Path.Combine(appDataDirectory, "window-placement.json");
    }

    public void Apply(Window window) {
        var snapshot = TryLoad();
        if (snapshot is null || !IsValid(snapshot)) {
            return;
        }

        var bounds = new Rect(
            snapshot.Left,
            snapshot.Top,
            Math.Max(window.MinWidth, snapshot.Width),
            Math.Max(window.MinHeight, snapshot.Height));

        if (!IsSavedScreenAvailable(snapshot.ScreenDeviceName)) {
            bounds = CenterOnPrimaryScreen(bounds);
        }

        bounds = ClampToVirtualScreen(bounds);

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = bounds.Left;
        window.Top = bounds.Top;
        window.Width = bounds.Width;
        window.Height = bounds.Height;

        if (snapshot.IsMaximized) {
            window.SourceInitialized += OnSourceInitialized;
            return;
        }

        void OnSourceInitialized(object? sender, EventArgs e) {
            window.SourceInitialized -= OnSourceInitialized;
            window.WindowState = WindowState.Maximized;
        }
    }

    public void Save(Window window) {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;

        if (bounds.Width <= 0 || bounds.Height <= 0) {
            return;
        }

        var snapshot = new WindowPlacementSnapshot {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = window.WindowState == WindowState.Maximized,
            ScreenDeviceName = TryGetCurrentScreenDeviceName(window),
        };

        try {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(_stateFilePath, json);
        }
        catch {
            // Keep app shutdown resilient even if persistence fails.
        }
    }

    private WindowPlacementSnapshot? TryLoad() {
        if (!File.Exists(_stateFilePath)) {
            return null;
        }

        try {
            var json = File.ReadAllText(_stateFilePath);
            return JsonSerializer.Deserialize<WindowPlacementSnapshot>(json, JsonOptions);
        }
        catch {
            return null;
        }
    }

    private static bool IsValid(WindowPlacementSnapshot snapshot) {
        if (double.IsNaN(snapshot.Left) || double.IsNaN(snapshot.Top)) {
            return false;
        }

        if (double.IsNaN(snapshot.Width) || double.IsNaN(snapshot.Height)) {
            return false;
        }

        if (double.IsInfinity(snapshot.Left) || double.IsInfinity(snapshot.Top)) {
            return false;
        }

        if (double.IsInfinity(snapshot.Width) || double.IsInfinity(snapshot.Height)) {
            return false;
        }

        return snapshot.Width > 0 && snapshot.Height > 0;
    }

    private static bool IsSavedScreenAvailable(string? screenDeviceName) {
        if (string.IsNullOrWhiteSpace(screenDeviceName)) {
            return true;
        }

        return Forms.Screen.AllScreens.Any(screen =>
            string.Equals(screen.DeviceName, screenDeviceName, StringComparison.OrdinalIgnoreCase));
    }

    private static Rect CenterOnPrimaryScreen(Rect bounds) {
        var workArea = SystemParameters.WorkArea;
        var centeredLeft = workArea.Left + Math.Max(0, (workArea.Width - bounds.Width) / 2d);
        var centeredTop = workArea.Top + Math.Max(0, (workArea.Height - bounds.Height) / 2d);
        return new Rect(centeredLeft, centeredTop, bounds.Width, bounds.Height);
    }

    private static Rect ClampToVirtualScreen(Rect bounds) {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualWidth = SystemParameters.VirtualScreenWidth;
        var virtualHeight = SystemParameters.VirtualScreenHeight;

        if (virtualWidth <= 0 || virtualHeight <= 0) {
            return bounds;
        }

        var clampedWidth = Math.Min(bounds.Width, virtualWidth);
        var clampedHeight = Math.Min(bounds.Height, virtualHeight);
        var maxLeft = virtualLeft + virtualWidth - clampedWidth;
        var maxTop = virtualTop + virtualHeight - clampedHeight;
        var clampedLeft = Math.Min(Math.Max(bounds.Left, virtualLeft), maxLeft);
        var clampedTop = Math.Min(Math.Max(bounds.Top, virtualTop), maxTop);

        return new Rect(clampedLeft, clampedTop, clampedWidth, clampedHeight);
    }

    private static string? TryGetCurrentScreenDeviceName(Window window) {
        try {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) {
                return null;
            }

            return Forms.Screen.FromHandle(handle).DeviceName;
        }
        catch {
            return null;
        }
    }

    private sealed class WindowPlacementSnapshot {
        public double Left { get; init; }
        public double Top { get; init; }
        public double Width { get; init; }
        public double Height { get; init; }
        public bool IsMaximized { get; init; }
        public string? ScreenDeviceName { get; init; }
    }
}
