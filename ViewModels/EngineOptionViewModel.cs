using AudioScript.Abstractions;

namespace AudioScript.ViewModels;

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



