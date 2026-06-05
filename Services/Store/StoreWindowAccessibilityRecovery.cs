using System.Windows;

namespace AudioScript.Services.Store;

public static class StoreWindowAccessibilityRecovery
{
    public static void RecoverAfterStoreFlow(
        Window? initiatingWindow,
        Window? mainWindow,
        Action<string, Exception>? logException = null)
    {
        RecoverWindow(initiatingWindow, "premium initiating window recovery", logException);
        RecoverWindow(mainWindow, "premium main window recovery", logException);
    }

    private static void RecoverWindow(
        Window? window,
        string logContext,
        Action<string, Exception>? logException)
    {
        if (window is null)
        {
            return;
        }

        try
        {
            window.IsEnabled = true;
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            if (!window.IsVisible)
            {
                window.Show();
            }

            window.Activate();
            _ = window.Focus();
        }
        catch (Exception ex)
        {
            logException?.Invoke(logContext, ex);
        }
    }
}
