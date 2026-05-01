using AudioScript.Abstractions;
using AudioScript.Audio;

namespace AudioScript.Services;

public sealed class ChunkedSpeakerDiarizationService {
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

    private void Log(string message) {
        _processLogService.Log("SpeakerDiarization", message);
    }

    private static string FormatDuration(TimeSpan duration) {
        return duration.TotalHours >= 1
            ? duration.ToString(@"hh\:mm\:ss")
            : duration.ToString(@"mm\:ss");
    }
}
