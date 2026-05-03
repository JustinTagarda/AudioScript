using AudioScript.Abstractions;
using AudioScript.Audio;

namespace AudioScript.Services;

public sealed class ChunkedSpeakerDiarizationService {
    public static readonly TimeSpan SpeakerDiarizationChunkDuration = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SpeakerDiarizationOverlapDuration = TimeSpan.FromSeconds(10);

    private readonly AudioChunkingService _audioChunkingService;
    private readonly OfflineSpeakerDiarizationService _requestService;
    private readonly ProcessLogService _processLogService;

    public ChunkedSpeakerDiarizationService(
        AudioChunkingService audioChunkingService,
        OfflineSpeakerDiarizationService requestService,
        ProcessLogService processLogService) {
        _audioChunkingService = audioChunkingService;
        _requestService = requestService;
        _processLogService = processLogService;
    }

    public async Task<SpeakerDiarizationResult> DiarizeAudioFileAsync(
        string audioFilePath,
        TranscriptionResult transcriptionResult,
        CancellationToken cancellationToken,
        IProgress<TranscriptionProgressSnapshot>? progress = null) {
        AudioSourceInfo sourceInfo = _audioChunkingService.GetSourceInfo(audioFilePath);
        Log(
            $"Speaker diarization will run after transcription for '{sourceInfo.Name}' " +
            $"({FormatDuration(sourceInfo.Duration)}, {sourceInfo.FileSizeBytes:N0} bytes).");

        return await _requestService.ApplySpeakerLabelsAsync(
            sourceInfo.FullPath,
            transcriptionResult,
            cancellationToken,
            progress);
    }

    public ChunkedAudioFile PrepareIncrementalDiarizationChunks(string audioFilePath) {
        AudioSourceInfo sourceInfo = _audioChunkingService.GetSourceInfo(audioFilePath);
        Log(
            $"Speaker diarization incremental chunks prepared for '{sourceInfo.Name}' " +
            $"({FormatDuration(sourceInfo.Duration)}, {sourceInfo.FileSizeBytes:N0} bytes).");
        return _audioChunkingService.PrepareFixedChunks(
            sourceInfo,
            SpeakerDiarizationChunkDuration,
            SpeakerDiarizationOverlapDuration);
    }

    public async Task<SpeakerDiarizationResult> DiarizeChunkAsync(
        AudioChunkFile chunkFile,
        TranscriptionResult transcriptionResult,
        CancellationToken cancellationToken,
        IProgress<TranscriptionProgressSnapshot>? progress = null) {
        ArgumentNullException.ThrowIfNull(chunkFile);
        ArgumentNullException.ThrowIfNull(transcriptionResult);
        Log(
            $"Speaker diarization chunk {chunkFile.Plan.Index + 1} started " +
            $"[{FormatDuration(chunkFile.Plan.RequestStart)} - {FormatDuration(chunkFile.Plan.RequestEnd)}].");
        return await _requestService.ApplySpeakerLabelsAsync(
            chunkFile.FilePath,
            transcriptionResult,
            cancellationToken,
            progress);
    }

    private void Log(string message) {
        _processLogService.Log("SpeakerDiarization", message);
    }

    private static string FormatDuration(TimeSpan duration) {
        return duration.TotalHours >= 1
            ? duration.ToString(@"hh\:mm\:ss")
            : duration.ToString(@"mm\:ss");
    }
}
