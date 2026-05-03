namespace AudioScript.Audio;

public enum LiveAudioSourceKind
{
    Microphone,
    DefaultPlayback,
    MicrophoneAndDefaultPlayback,
    AudioScriptPlayback,
    MicrophoneAndAudioScriptPlayback,
}

public sealed record AudioInputDeviceOption(
    LiveAudioSourceKind Kind,
    int DeviceNumber,
    string Name
)
{
    public override string ToString() => Name;
}
