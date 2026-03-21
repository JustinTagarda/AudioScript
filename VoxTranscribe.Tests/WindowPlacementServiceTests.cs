using System.Windows;
using VoxTranscribe.Services;
using Xunit;

namespace VoxTranscribe.Tests;

public sealed class WindowPlacementServiceTests {
    [Fact]
    public void ResolveRestoredBounds_KeepsClippedWindowOnSavedSecondaryMonitor() {
        Rect requestedBounds = new(3740, 120, 700, 800);
        WindowPlacementService.DisplayAreaSnapshot[] screens = [
            new("\\\\.\\DISPLAY1", new Rect(0, 0, 1920, 1040), true),
            new("\\\\.\\DISPLAY2", new Rect(1920, 0, 1920, 1040), false),
        ];

        Rect restoredBounds = WindowPlacementService.ResolveRestoredBounds(
            requestedBounds,
            savedScreenDeviceName: "\\\\.\\DISPLAY2",
            screens);

        Assert.True(restoredBounds.Left >= 2140);
        Assert.True(restoredBounds.Left <= 3680);
        Assert.Equal(120, restoredBounds.Top);
        Assert.Equal(700, restoredBounds.Width);
        Assert.Equal(800, restoredBounds.Height);
    }

    [Fact]
    public void ResolveRestoredBounds_FallsBackToPrimaryScreenWhenSavedMonitorIsMissing() {
        Rect requestedBounds = new(2600, 180, 900, 700);
        WindowPlacementService.DisplayAreaSnapshot[] screens = [
            new("\\\\.\\DISPLAY1", new Rect(0, 0, 1920, 1040), true),
        ];

        Rect restoredBounds = WindowPlacementService.ResolveRestoredBounds(
            requestedBounds,
            savedScreenDeviceName: "\\\\.\\DISPLAY2",
            screens);

        Assert.Equal(1700, restoredBounds.Left);
        Assert.Equal(180, restoredBounds.Top);
        Assert.Equal(900, restoredBounds.Width);
        Assert.Equal(700, restoredBounds.Height);
    }

    [Fact]
    public void EnsureVisibleWithinWorkingArea_PreservesAccessibleTitleBarWhenWindowIsPartiallyOffscreen() {
        Rect adjustedBounds = WindowPlacementService.EnsureVisibleWithinWorkingArea(
            new Rect(3890, -40, 640, 760),
            new Rect(1920, 0, 1920, 1040));

        Assert.Equal(3620, adjustedBounds.Left);
        Assert.Equal(0, adjustedBounds.Top);
        Assert.Equal(640, adjustedBounds.Width);
        Assert.Equal(760, adjustedBounds.Height);
    }
}
