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
