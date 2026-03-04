using AudioTranscript.Abstractions;

namespace AudioTranscript.ViewModels;

public sealed class EngineOptionViewModel {
    public EngineOptionViewModel(ITranscriptionEngine engine) {
        Engine = engine;
    }

    public ITranscriptionEngine Engine { get; }

    public string DisplayName => Engine.DisplayName;

    public override string ToString() {
        return DisplayName;
    }
}