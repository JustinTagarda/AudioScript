namespace AudioScript.Services;

public static class AudioStorageKinds
{
    public const string ImportedFile = nameof(ImportedFile);
    public const string LiveRecordingManifest = nameof(LiveRecordingManifest);
}

public static class LiveRecordingManifestStatuses
{
    public const string Recording = nameof(Recording);
    public const string Completed = nameof(Completed);
    public const string Interrupted = nameof(Interrupted);
}

public sealed class LiveRecordingManifest
{
    public string Status { get; set; } = LiveRecordingManifestStatuses.Recording;

    public string Format { get; set; } = "wav-pcm";

    public int SampleRate { get; set; } = 16000;

    public int BitsPerSample { get; set; } = 16;

    public int Channels { get; set; } = 1;

    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? EndedUtc { get; set; }

    public string InputSource { get; set; } = string.Empty;

    public double TotalDurationSeconds { get; set; }

    public long TotalFileSizeBytes { get; set; }

    public string? ErrorMessage { get; set; }

    public List<LiveRecordingSegmentManifest> Segments { get; set; } = new();
}

public sealed class LiveRecordingSegmentManifest
{
    public string RelativePath { get; set; } = string.Empty;

    public double StartSeconds { get; set; }

    public double DurationSeconds { get; set; }

    public long FileSizeBytes { get; set; }
}

public sealed class LiveRecordingSegmentFinalizedEventArgs : EventArgs
{
    public LiveRecordingSegmentFinalizedEventArgs(
        LiveRecordingSegmentManifest segment,
        string segmentPath)
    {
        Segment = segment;
        SegmentPath = segmentPath;
    }

    public LiveRecordingSegmentManifest Segment { get; }

    public string SegmentPath { get; }
}
