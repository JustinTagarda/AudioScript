using System.IO;

namespace AudioScript.Services;

public sealed class AppDataMigrationService
{
    private readonly AppDataPathProvider _paths;
    private readonly ProcessLogService _processLogService;

    public AppDataMigrationService(AppDataPathProvider paths, ProcessLogService processLogService)
    {
        _paths = paths;
        _processLogService = processLogService;
    }

    public AppDataMigrationResult MigrateLegacyData(IReadOnlyList<WhisperEngineModelDefinition> modelDefinitions)
    {
        int migrated = 0;
        int skipped = 0;
        var failedPaths = new List<string>();

        Directory.CreateDirectory(_paths.RootPath);
        Directory.CreateDirectory(_paths.ModelsPath);
        Directory.CreateDirectory(_paths.SessionsPath);
        Directory.CreateDirectory(_paths.LogsPath);
        Directory.CreateDirectory(_paths.TempPath);
        Directory.CreateDirectory(_paths.SettingsPath);

        MigrateOptionalModelFiles(modelDefinitions, ref migrated, ref skipped, failedPaths);
        CopyFileIfMissing(
            Path.Combine(_paths.LegacyRootPath, "app-preferences.json"),
            _paths.SettingsFilePath,
            ref migrated,
            ref skipped,
            failedPaths);
        CopyFileIfMissing(
            Path.Combine(_paths.LegacyRootPath, "window-placement.json"),
            Path.Combine(_paths.SettingsPath, "window-placement.json"),
            ref migrated,
            ref skipped,
            failedPaths);
        CopyDirectoryContentsIfMissing(
            Path.Combine(_paths.LegacyRootPath, "Sessions"),
            _paths.SessionsPath,
            ref migrated,
            ref skipped,
            failedPaths);
        CopyDirectoryContentsIfMissing(
            Path.Combine(_paths.LegacyRootPath, "Logs"),
            _paths.LogsPath,
            ref migrated,
            ref skipped,
            failedPaths);

        var result = new AppDataMigrationResult(
            MigratedFileCount: migrated,
            SkippedFileCount: skipped,
            FailedPaths: failedPaths,
            IsPartialFailure: failedPaths.Count > 0);

        if (migrated > 0 || skipped > 0 || failedPaths.Count > 0)
        {
            _processLogService.Log(
                "AppData",
                $"Legacy app data migration completed. migrated={migrated:N0}, skipped={skipped:N0}, failed={failedPaths.Count:N0}, packaged={_paths.IsPackaged}.");
        }

        return result;
    }

    private void MigrateOptionalModelFiles(
        IReadOnlyList<WhisperEngineModelDefinition> modelDefinitions,
        ref int migrated,
        ref int skipped,
        List<string> failedPaths)
    {
        string legacyModelsPath = Path.Combine(_paths.LegacyRootPath, "Models");
        if (AreSamePath(legacyModelsPath, _paths.ModelsPath) || !Directory.Exists(legacyModelsPath))
        {
            return;
        }

        foreach (WhisperEngineModelDefinition definition in modelDefinitions.Where(model => !model.IsBundled))
        {
            string sourcePath = Path.Combine(legacyModelsPath, definition.FileName);
            string destinationPath = Path.Combine(_paths.ModelsPath, definition.FileName);
            MoveFileIfMissing(sourcePath, destinationPath, ref migrated, ref skipped, failedPaths);
        }
    }

    private static void CopyDirectoryContentsIfMissing(
        string sourceDirectoryPath,
        string destinationDirectoryPath,
        ref int migrated,
        ref int skipped,
        List<string> failedPaths)
    {
        if (AreSamePath(sourceDirectoryPath, destinationDirectoryPath) || !Directory.Exists(sourceDirectoryPath))
        {
            return;
        }

        foreach (string sourcePath in Directory.EnumerateFiles(sourceDirectoryPath, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectoryPath, sourcePath);
            string destinationPath = Path.Combine(destinationDirectoryPath, relativePath);
            CopyFileIfMissing(sourcePath, destinationPath, ref migrated, ref skipped, failedPaths);
        }
    }

    private static void MoveFileIfMissing(
        string sourcePath,
        string destinationPath,
        ref int migrated,
        ref int skipped,
        List<string> failedPaths)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        if (File.Exists(destinationPath))
        {
            skipped++;
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Move(sourcePath, destinationPath);
            migrated++;
        }
        catch
        {
            failedPaths.Add(sourcePath);
        }
    }

    private static void CopyFileIfMissing(
        string sourcePath,
        string destinationPath,
        ref int migrated,
        ref int skipped,
        List<string> failedPaths)
    {
        if (AreSamePath(sourcePath, destinationPath) || !File.Exists(sourcePath))
        {
            return;
        }

        if (File.Exists(destinationPath))
        {
            skipped++;
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
            migrated++;
        }
        catch
        {
            failedPaths.Add(sourcePath);
        }
    }

    private static bool AreSamePath(string firstPath, string secondPath)
    {
        return string.Equals(
            Path.GetFullPath(firstPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(secondPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record AppDataMigrationResult(
    int MigratedFileCount,
    int SkippedFileCount,
    IReadOnlyList<string> FailedPaths,
    bool IsPartialFailure);
