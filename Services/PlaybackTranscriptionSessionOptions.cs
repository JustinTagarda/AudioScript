namespace AudioScript.Services;

public sealed record PlaybackTranscriptionSessionOptions(
    TimeSpan MinimumSegmentDuration,
    TimeSpan InterimWindowDuration,
    TimeSpan InterimCadence,
    TimeSpan FinalWindowDuration,
    TimeSpan PollInterval,
    double MinimumPeakLevel = 0) {
    public static PlaybackTranscriptionSessionOptions Default { get; } = new(
        MinimumSegmentDuration: TimeSpan.FromSeconds(1.5),
        InterimWindowDuration: TimeSpan.FromSeconds(4),
        InterimCadence: TimeSpan.FromSeconds(2),
        FinalWindowDuration: TimeSpan.FromSeconds(8),
        PollInterval: TimeSpan.FromMilliseconds(250),
        MinimumPeakLevel: 0);

    public PlaybackTranscriptionSessionOptions Validate() {
        if (MinimumSegmentDuration <= TimeSpan.Zero) {
            throw new InvalidOperationException("Minimum playback segment duration must be greater than zero.");
        }

        if (InterimWindowDuration < MinimumSegmentDuration) {
            throw new InvalidOperationException("Interim playback window must be at least as large as the minimum segment duration.");
        }

        if (InterimCadence <= TimeSpan.Zero) {
            throw new InvalidOperationException("Interim playback cadence must be greater than zero.");
        }

        if (FinalWindowDuration < InterimWindowDuration) {
            throw new InvalidOperationException("Final playback window must be at least as large as the interim window.");
        }

        if (PollInterval <= TimeSpan.Zero) {
            throw new InvalidOperationException("Playback transcription poll interval must be greater than zero.");
        }

        if (MinimumPeakLevel < 0 || MinimumPeakLevel > 1) {
            throw new InvalidOperationException("Minimum playback peak level must be between 0 and 1.");
        }

        return this;
    }
}



