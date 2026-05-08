using System.IO;
using AudioScript.Abstractions;
using Whisper.net.Ggml;

namespace AudioScript.Services;

public sealed class WhisperModelManager
{
    private const int InstallRetryCount = 5;
    private const int CopyBufferSize = 128 * 1024;
    private const string WhisperSmallAssetId = "whisper-small";

    private static readonly IReadOnlyList<WhisperEngineModelDefinition> ModelDefinitions = new[] {
        new WhisperEngineModelDefinition(
            Id: TranscriptionModelCatalog.WhisperSmall,
            DisplayName: "Whisper small",
            FileName: "ggml-small.bin",
            SizeText: "about 466 MB",
            Description: "Required default offline model downloaded into local app storage on first use.",
            Benefits: "Good minimum quality for local transcription while staying smaller than medium.",
            Notes: "Install from Settings before using Whisper transcription on a new device.",
            GgmlType: GgmlType.Small,
            ExpectedBytes: 487_601_967,
            IsBundled: false,
            IsFixedInstalled: false),
        new WhisperEngineModelDefinition(
            Id: TranscriptionModelCatalog.WhisperMedium,
            DisplayName: "Whisper medium",
            FileName: "ggml-medium.bin",
            SizeText: "about 1.5 GB",
            Description: "Higher accuracy for noisy, accented, or multilingual audio.",
            Benefits: "Stronger quality without the full large-v3 footprint.",
            Notes: "Needs more disk, RAM, and transcription time than small.",
            GgmlType: GgmlType.Medium,
            ExpectedBytes: 1_534_000_000,
            IsBundled: false,
            IsFixedInstalled: false),
        new WhisperEngineModelDefinition(
            Id: TranscriptionModelCatalog.WhisperLargeV3,
            DisplayName: "Whisper large-v3",
            FileName: "ggml-large-v3.bin",
            SizeText: "about 3.0 GB",
            Description: "Highest quality Whisper model in the v1 offline list.",
            Benefits: "Best accuracy for difficult audio when speed and size are secondary.",
            Notes: "Largest install and slowest local transcription option.",
            GgmlType: GgmlType.LargeV3,
            ExpectedBytes: 3_100_000_000,
            IsBundled: false,
            IsFixedInstalled: false),
        new WhisperEngineModelDefinition(
            Id: TranscriptionModelCatalog.WhisperLargeV3Turbo,
            DisplayName: "Whisper large-v3-turbo",
            FileName: "ggml-large-v3-turbo.bin",
            SizeText: "about 1.6 GB",
            Description: "Faster large-v3 family model with lower local cost.",
            Benefits: "Good balance when large-v3 is too heavy but quality still matters.",
            Notes: "Usually faster than full large-v3, with some quality tradeoff.",
            GgmlType: GgmlType.LargeV3Turbo,
            ExpectedBytes: 1_620_000_000,
            IsBundled: false,
            IsFixedInstalled: false),
    };

    private readonly ProcessLogService _processLogService;
    private readonly string _optionalModelsDirectoryPath;
    private readonly IAssetProvisioningService? _assetProvisioningService;

    public WhisperModelManager(
        ProcessLogService processLogService,
        string? optionalModelsDirectoryPath = null,
        string? bundledModelsDirectoryPath = null,
        IAssetProvisioningService? assetProvisioningService = null)
    {
        _processLogService = processLogService;
        _optionalModelsDirectoryPath = string.IsNullOrWhiteSpace(optionalModelsDirectoryPath)
            ? AppDataPathProvider.Create().ModelsPath
            : Path.GetFullPath(optionalModelsDirectoryPath);
        _assetProvisioningService = assetProvisioningService;
    }

    public IReadOnlyList<WhisperEngineModelDefinition> Models => ModelDefinitions;

    public IReadOnlyList<TranscriptionModelOption> GetSelectableTranscriptionModels()
    {
        var selectable = ModelDefinitions
            .Where(model => IsModelInstalled(model.Id))
            .Select(model => TranscriptionModelCatalog.Find(model.Id))
            .OfType<TranscriptionModelOption>()
            .ToArray();

        return selectable;
    }

    public bool IsModelInstalled(string modelId)
    {
        WhisperEngineModelDefinition definition = GetDefinition(modelId);
        return File.Exists(ResolveModelPath(definition));
    }

    public string ResolveInstalledModelPath(string modelId)
    {
        WhisperEngineModelDefinition definition = GetDefinition(modelId);
        string modelPath = ResolveModelPath(definition);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                $"Whisper model '{definition.DisplayName}' is not installed. Open Settings to install the model and try again.",
                modelPath);
        }

        return modelPath;
    }

    public async Task InstallModelAsync(
        string modelId,
        IProgress<WhisperModelInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        WhisperEngineModelDefinition definition = GetDefinition(modelId);
        string modelPath = ResolveModelPath(definition);
        if (File.Exists(modelPath))
        {
            Report(progress, "Installed", definition.ExpectedBytes ?? 0, definition.ExpectedBytes, 100);
            return;
        }

        if (string.Equals(definition.Id, TranscriptionModelCatalog.WhisperSmall, StringComparison.OrdinalIgnoreCase))
        {
            if (_assetProvisioningService is null)
            {
                throw new InvalidOperationException("Whisper small provisioning is not configured.");
            }

            var adapter = progress is null
                ? null
                : new Progress<AssetProvisioningProgress>(assetProgress =>
                    Report(progress, assetProgress.Status, assetProgress.BytesReceived, assetProgress.TotalBytes, assetProgress.Percent));
            await _assetProvisioningService.InstallAssetAsync(WhisperSmallAssetId, adapter, cancellationToken);
            return;
        }

        if (definition.GgmlType is null)
        {
            throw new InvalidOperationException($"{definition.DisplayName} cannot be downloaded.");
        }

        Directory.CreateDirectory(_optionalModelsDirectoryPath);
        string tempPath = $"{modelPath}.{Guid.NewGuid():N}.download";
        DeleteTemporaryFile(tempPath);
        Log($"Installing {definition.DisplayName}. tempPath='{tempPath}', finalPath='{modelPath}'.");

        try
        {
            Report(progress, "Connecting...", 0, definition.ExpectedBytes, 0);
            await using Stream modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                definition.GgmlType.Value,
                QuantizationType.NoQuantization,
                cancellationToken);
            long totalRead;
            await using (FileStream fileStream = File.Open(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[CopyBufferSize];
                totalRead = 0;
                int read;
                while ((read = await modelStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    totalRead += read;
                    double percent = definition.ExpectedBytes is > 0
                        ? Math.Min(99, totalRead * 100d / definition.ExpectedBytes.Value)
                        : 0;
                    Report(progress, "Downloading...", totalRead, definition.ExpectedBytes, percent);
                }

                await fileStream.FlushAsync(cancellationToken);
            }

            Report(progress, "Finalizing install...", totalRead, definition.ExpectedBytes, 99);
            await FinalizeModelInstallAsync(tempPath, modelPath, cancellationToken);
            Report(progress, "Installed", new FileInfo(modelPath).Length, definition.ExpectedBytes, 100);
            Log($"Installed {definition.DisplayName}. path='{modelPath}'.");
        }
        catch (OperationCanceledException)
        {
            DeleteTemporaryFile(tempPath);
            Log($"Canceled installation for {definition.DisplayName}.");
            throw;
        }
        catch
        {
            DeleteTemporaryFile(tempPath);
            throw;
        }
    }

    public WhisperModelUninstallResult UninstallModel(string modelId)
    {
        WhisperEngineModelDefinition definition = GetDefinition(modelId);
        if (definition.IsFixedInstalled)
        {
            throw new InvalidOperationException($"{definition.DisplayName} cannot be removed.");
        }

        string modelPath = ResolveModelPath(definition);
        if (!File.Exists(modelPath))
        {
            Log($"Uninstall skipped for {definition.DisplayName}; file was already missing. path='{modelPath}'.");
            return new WhisperModelUninstallResult(
                definition.Id,
                definition.DisplayName,
                modelPath,
                DeletedBytes: 0,
                WasDeleted: false);
        }

        long deletedBytes = new FileInfo(modelPath).Length;
        File.Delete(modelPath);
        Log($"Uninstalled {definition.DisplayName}. deletedBytes={deletedBytes:N0}, path='{modelPath}'.");
        return new WhisperModelUninstallResult(
            definition.Id,
            definition.DisplayName,
            modelPath,
            deletedBytes,
            WasDeleted: true);
    }

    public WhisperEngineModelDefinition GetDefinition(string modelId)
    {
        string trimmed = modelId?.Trim() ?? string.Empty;
        return ModelDefinitions.FirstOrDefault(model =>
                string.Equals(model.Id, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown Whisper model '{modelId}'.");
    }

    public string ResolveModelPath(string modelId)
    {
        return ResolveModelPath(GetDefinition(modelId));
    }

    public long GetInstalledModelSize(string modelId)
    {
        string modelPath = ResolveModelPath(modelId);
        return File.Exists(modelPath)
            ? new FileInfo(modelPath).Length
            : 0;
    }

    private string ResolveModelPath(WhisperEngineModelDefinition definition)
    {
        return Path.Combine(_optionalModelsDirectoryPath, definition.FileName);
    }

    private async Task FinalizeModelInstallAsync(
        string tempPath,
        string modelPath,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (int attempt = 1; attempt <= InstallRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (File.Exists(modelPath))
                {
                    File.Delete(modelPath);
                }

                File.Move(tempPath, modelPath);
                return;
            }
            catch (IOException ex)
            {
                lastException = ex;
                Log($"Whisper model install attempt {attempt}/{InstallRetryCount} failed: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                lastException = ex;
                Log($"Whisper model install attempt {attempt}/{InstallRetryCount} failed: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
        }

        throw new IOException(
            $"Unable to finalize Whisper model install to '{modelPath}' after {InstallRetryCount} attempts.",
            lastException);
    }

    private static void Report(
        IProgress<WhisperModelInstallProgress>? progress,
        string status,
        long bytesReceived,
        long? totalBytes,
        double percent)
    {
        progress?.Report(new WhisperModelInstallProgress(status, bytesReceived, totalBytes, percent));
    }

    private static void DeleteTemporaryFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Best-effort cleanup for canceled or failed model downloads.
        }
    }

    private void Log(string message)
    {
        _processLogService.Log("WhisperModels", message);
    }
}
