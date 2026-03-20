using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Path = System.IO.Path;
using Directory = System.IO.Directory;
using File = System.IO.File;

namespace VoxTranscriber.Services;

public sealed class WindowPlacementService {
    private const double MinimumVisibleTitleBarWidth = 220d;
    private const double MinimumVisibleTitleBarHeight = 60d;
    private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
    };

    private readonly string _stateFilePath;
    private readonly TimeSpan _saveDebounceDelay;
    private Window? _trackedWindow;
    private DispatcherTimer? _saveDebounceTimer;

    public WindowPlacementService(string? stateFilePath = null, TimeSpan? saveDebounceDelay = null) {
        if (!string.IsNullOrWhiteSpace(stateFilePath)) {
            _stateFilePath = Path.GetFullPath(stateFilePath);
        }
        else {
            var appDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VoxTranscriber");
            _stateFilePath = Path.Combine(appDataDirectory, "window-placement.json");
        }

        _saveDebounceDelay = saveDebounceDelay ?? TimeSpan.FromMilliseconds(350);
    }

    public void Apply(Window window) {
        var snapshot = TryLoad();
        if (snapshot is null || !IsValid(snapshot)) {
            return;
        }

        var requestedBounds = new Rect(
            snapshot.Left,
            snapshot.Top,
            Math.Max(window.MinWidth, snapshot.Width),
            Math.Max(window.MinHeight, snapshot.Height));

        var bounds = ResolveRestoredBounds(
            requestedBounds,
            snapshot.ScreenDeviceName,
            GetAvailableScreens());

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

    public void Attach(Window window) {
        if (ReferenceEquals(_trackedWindow, window)) {
            return;
        }

        Detach();

        _trackedWindow = window;
        _saveDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) {
            Interval = _saveDebounceDelay,
        };
        _saveDebounceTimer.Tick += OnSaveDebounceTimerTick;

        window.LocationChanged += OnTrackedWindowBoundsChanged;
        window.SizeChanged += OnTrackedWindowSizeChanged;
        window.StateChanged += OnTrackedWindowStateChanged;
        window.Closing += OnTrackedWindowClosing;
        window.Closed += OnTrackedWindowClosed;
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
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            WriteAllTextAtomic(_stateFilePath, json);
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

    internal static Rect ResolveRestoredBounds(
        Rect requestedBounds,
        string? savedScreenDeviceName,
        IReadOnlyList<DisplayAreaSnapshot> screens) {
        if (screens.Count == 0) {
            return requestedBounds;
        }

        DisplayAreaSnapshot targetScreen = ResolveTargetScreen(savedScreenDeviceName, requestedBounds, screens);
        return EnsureVisibleWithinWorkingArea(requestedBounds, targetScreen.WorkingArea);
    }

    internal static Rect EnsureVisibleWithinWorkingArea(Rect bounds, Rect workingArea) {
        if (workingArea.Width <= 0 || workingArea.Height <= 0) {
            return bounds;
        }

        double width = Math.Min(bounds.Width, workingArea.Width);
        double height = Math.Min(bounds.Height, workingArea.Height);
        double visibleTitleWidth = Math.Min(width, MinimumVisibleTitleBarWidth);
        double visibleTitleHeight = Math.Min(height, MinimumVisibleTitleBarHeight);
        double minLeft = workingArea.Left - width + visibleTitleWidth;
        double maxLeft = workingArea.Right - visibleTitleWidth;
        double minTop = workingArea.Top;
        double maxTop = workingArea.Bottom - visibleTitleHeight;
        double left = Math.Min(Math.Max(bounds.Left, minLeft), maxLeft);
        double top = Math.Min(Math.Max(bounds.Top, minTop), maxTop);

        return new Rect(left, top, width, height);
    }

    internal static DisplayAreaSnapshot ResolveTargetScreen(
        string? savedScreenDeviceName,
        Rect requestedBounds,
        IReadOnlyList<DisplayAreaSnapshot> screens) {
        if (!string.IsNullOrWhiteSpace(savedScreenDeviceName)) {
            DisplayAreaSnapshot? savedScreen = screens.FirstOrDefault(screen =>
                string.Equals(screen.DeviceName, savedScreenDeviceName, StringComparison.OrdinalIgnoreCase));
            if (savedScreen is not null) {
                return savedScreen;
            }
        }

        System.Windows.Point centerPoint = new(
            requestedBounds.Left + (requestedBounds.Width / 2d),
            requestedBounds.Top + (requestedBounds.Height / 2d));
        DisplayAreaSnapshot? centerMatch = screens.FirstOrDefault(screen => screen.WorkingArea.Contains(centerPoint));
        if (centerMatch is not null) {
            return centerMatch;
        }

        DisplayAreaSnapshot? intersectionMatch = screens
            .Select(screen => new {
                Screen = screen,
                Area = CalculateIntersectionArea(screen.WorkingArea, requestedBounds),
            })
            .Where(candidate => candidate.Area > 0)
            .OrderByDescending(candidate => candidate.Area)
            .Select(candidate => candidate.Screen)
            .FirstOrDefault();

        if (intersectionMatch is not null) {
            return intersectionMatch;
        }

        return screens.FirstOrDefault(screen => screen.IsPrimary) ?? screens[0];
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

    private static IReadOnlyList<DisplayAreaSnapshot> GetAvailableScreens() {
        return Forms.Screen.AllScreens
            .Select(screen => new DisplayAreaSnapshot(
                screen.DeviceName,
                new Rect(
                    screen.WorkingArea.Left,
                    screen.WorkingArea.Top,
                    screen.WorkingArea.Width,
                    screen.WorkingArea.Height),
                screen.Primary))
            .ToArray();
    }

    private void OnTrackedWindowBoundsChanged(object? sender, EventArgs e) {
        QueueSave();
    }

    private void OnTrackedWindowSizeChanged(object? sender, SizeChangedEventArgs e) {
        QueueSave();
    }

    private void OnTrackedWindowStateChanged(object? sender, EventArgs e) {
        QueueSave();
    }

    private void OnTrackedWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e) {
        if (sender is Window window) {
            _saveDebounceTimer?.Stop();
            Save(window);
        }
    }

    private void OnTrackedWindowClosed(object? sender, EventArgs e) {
        Detach();
    }

    private void OnSaveDebounceTimerTick(object? sender, EventArgs e) {
        _saveDebounceTimer?.Stop();

        if (_trackedWindow is not null) {
            Save(_trackedWindow);
        }
    }

    private void QueueSave() {
        if (_trackedWindow is null || _saveDebounceTimer is null) {
            return;
        }

        if (!_trackedWindow.IsLoaded) {
            return;
        }

        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Start();
    }

    private void Detach() {
        if (_trackedWindow is null) {
            return;
        }

        _trackedWindow.LocationChanged -= OnTrackedWindowBoundsChanged;
        _trackedWindow.SizeChanged -= OnTrackedWindowSizeChanged;
        _trackedWindow.StateChanged -= OnTrackedWindowStateChanged;
        _trackedWindow.Closing -= OnTrackedWindowClosing;
        _trackedWindow.Closed -= OnTrackedWindowClosed;

        if (_saveDebounceTimer is not null) {
            _saveDebounceTimer.Stop();
            _saveDebounceTimer.Tick -= OnSaveDebounceTimerTick;
            _saveDebounceTimer = null;
        }

        _trackedWindow = null;
    }

    private static double CalculateIntersectionArea(Rect left, Rect right) {
        Rect intersection = Rect.Intersect(left, right);
        return intersection.IsEmpty ? 0d : intersection.Width * intersection.Height;
    }

    private static void WriteAllTextAtomic(string targetPath, string content) {
        string directory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);

        string tempPath = Path.Combine(directory, $"{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, content);

        try {
            if (File.Exists(targetPath)) {
                File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else {
                File.Move(tempPath, targetPath);
            }
        }
        finally {
            if (File.Exists(tempPath)) {
                File.Delete(tempPath);
            }
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

    internal sealed record DisplayAreaSnapshot(
        string DeviceName,
        Rect WorkingArea,
        bool IsPrimary);
}


