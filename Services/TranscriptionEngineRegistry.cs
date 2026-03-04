using AudioTranscript.Abstractions;

namespace AudioTranscript.Services;

public sealed class TranscriptionEngineRegistry {
    private readonly IReadOnlyList<ITranscriptionEngine> _engines;

    public TranscriptionEngineRegistry(IEnumerable<ITranscriptionEngine> engines) {
        _engines = engines.ToList().AsReadOnly();
    }

    public IReadOnlyList<ITranscriptionEngine> Engines => _engines;

    public ITranscriptionEngine? GetById(string id) {
        return _engines.FirstOrDefault(engine =>
            string.Equals(engine.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}