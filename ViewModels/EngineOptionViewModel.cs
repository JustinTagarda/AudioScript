using VoxTranscriber.Abstractions;

namespace VoxTranscriber.ViewModels;

public sealed class EngineOptionViewModel {
    public EngineOptionViewModel(TranscriptionModelOption model) {
        Model = model;
    }

    public TranscriptionModelOption Model { get; }

    public string Id => Model.Id;

    public string DisplayName => Model.DisplayName;

    public override string ToString() {
        return DisplayName;
    }
}


