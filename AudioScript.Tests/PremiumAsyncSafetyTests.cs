using System.IO;
using Xunit;

namespace AudioScript.Tests;

public sealed class PremiumAsyncSafetyTests
{
    [Fact]
    public void PremiumPaths_DoNotUseSyncOverAsyncPatterns()
    {
        string repoRoot = ResolveRepoRoot();
        string[] scopedFiles =
        {
            Path.Combine(repoRoot, "MainWindow.xaml.cs"),
            Path.Combine(repoRoot, "SettingsWindow.xaml.cs"),
            Path.Combine(repoRoot, "Services", "AppEntitlementModels.cs"),
        };

        string[] blockedPatterns =
        {
            ".Wait(",
            ".GetAwaiter().GetResult()",
        };

        foreach (string file in scopedFiles)
        {
            string text = File.ReadAllText(file);
            foreach (string pattern in blockedPatterns)
            {
                Assert.DoesNotContain(pattern, text);
            }
        }
    }

    [Fact]
    public void PremiumUiFlow_SettingsWindow_UsesConfirmationAndOwnerBinding()
    {
        string repoRoot = ResolveRepoRoot();
        string code = File.ReadAllText(Path.Combine(repoRoot, "SettingsWindow.xaml.cs"));

        Assert.Contains("ConfirmationDialogWindow(", code, StringComparison.Ordinal);
        Assert.Contains("StorePurchaseOwnerWindowBinding.BeginScope", code, StringComparison.Ordinal);
        Assert.Contains("StoreWindowAccessibilityRecovery.RecoverAfterStoreFlow", code, StringComparison.Ordinal);
    }

    [Fact]
    public void PremiumPurchaseFlow_DoesNotBlockAdministratorElevation()
    {
        string repoRoot = ResolveRepoRoot();
        string code = File.ReadAllText(Path.Combine(repoRoot, "Services", "AppEntitlementModels.cs"));

        Assert.DoesNotContain("IsProcessElevated", code, StringComparison.Ordinal);
        Assert.DoesNotContain("running as administrator", code, StringComparison.Ordinal);
    }

    [Fact]
    public void PremiumFooter_IncludesRestorePurchasesCommand()
    {
        string repoRoot = ResolveRepoRoot();
        string xaml = File.ReadAllText(Path.Combine(repoRoot, "AppStatusDisplay.xaml"));

        Assert.Contains("RestorePremiumPurchasesCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Restore\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void PremiumLifecycle_AppActivationRefreshesEntitlementAsync()
    {
        string repoRoot = ResolveRepoRoot();
        string code = File.ReadAllText(Path.Combine(repoRoot, "App.xaml.cs"));

        Assert.Contains("mainWindow.Activated += OnMainWindowActivatedRefreshPremium", code, StringComparison.Ordinal);
        Assert.Contains("await _mainViewModel.RefreshPremiumEntitlementAsync()", code, StringComparison.Ordinal);
    }

    private static string ResolveRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        DirectoryInfo? directory = new(current);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AudioScript.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
