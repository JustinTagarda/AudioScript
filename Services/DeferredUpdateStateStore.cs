using System.IO;
using System.Text.Json;

namespace AudioScript.Services;

public sealed class DeferredUpdateStateStore : IDeferredUpdateStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _stateFilePath;
    private readonly ProcessLogService _processLogService;

    public DeferredUpdateStateStore(string stateFilePath, ProcessLogService processLogService)
    {
        _stateFilePath = string.IsNullOrWhiteSpace(stateFilePath)
            ? throw new ArgumentException("State file path is required.", nameof(stateFilePath))
            : Path.GetFullPath(stateFilePath);
        _processLogService = processLogService ?? throw new ArgumentNullException(nameof(processLogService));
    }

    public async Task<DeferredUpdateState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_stateFilePath))
            {
                return null;
            }

            await using FileStream stream = new(
                _stateFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            return await JsonSerializer.DeserializeAsync<DeferredUpdateState>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _processLogService.LogException("AppUpdate", "deferred_state_load_failed", ex);
            return null;
        }
    }

    public async Task SaveAsync(DeferredUpdateState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        try
        {
            string? directoryPath = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            string tempFilePath = $"{_stateFilePath}.{Guid.NewGuid():N}.tmp";
            await using (FileStream stream = new(
                tempFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    state,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempFilePath, _stateFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _processLogService.LogException("AppUpdate", "deferred_state_save_failed", ex);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists(_stateFilePath))
            {
                File.Delete(_stateFilePath);
                _processLogService.Log("AppUpdate", "deferred_state_cleared");
            }
        }
        catch (Exception ex)
        {
            _processLogService.LogException("AppUpdate", "deferred_state_clear_failed", ex);
        }

        return Task.CompletedTask;
    }
}
