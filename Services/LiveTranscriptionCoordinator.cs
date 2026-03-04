using System.Threading.Channels;
using AudioTranscript.Abstractions;
using AudioTranscript.Audio;

namespace AudioTranscript.Services;

public sealed class LiveTranscriptionCoordinator : IAsyncDisposable {
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly object _stateLock = new();
    private Channel<byte[]>? _audioChannel;
    private Task? _forwardingTask;
    private IRealtimeTranscriptionSession? _session;
    private CancellationTokenSource? _runtimeCts;

    public LiveTranscriptionCoordinator(IAudioCaptureService audioCaptureService) {
        _audioCaptureService = audioCaptureService;
    }

    public bool IsRunning { get; private set; }

    public event EventHandler<TranscriptUpdate>? UpdateReceived;

    public event EventHandler<string>? StatusChanged;

    public async Task StartAsync(
        ITranscriptionEngine engine,
        TranscriptionRequest request,
        CancellationToken cancellationToken) {
        lock (_stateLock) {
            if (IsRunning) {
                return;
            }

            IsRunning = true;
        }

        try {
            _audioChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions {
                SingleReader = true,
                SingleWriter = false,
            });

            _runtimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _session = engine.CreateRealtimeSession(request);
            _session.UpdateReceived += OnSessionUpdate;
            _audioCaptureService.FrameCaptured += OnFrameCaptured;

            await _session.StartAsync(_runtimeCts.Token);
            _forwardingTask = Task.Run(() => ForwardAudioLoopAsync(_runtimeCts.Token), _runtimeCts.Token);
            await _audioCaptureService.StartDefaultPlaybackAsync(_runtimeCts.Token);

            StatusChanged?.Invoke(this, $"Live capture started (playback-only) with {engine.DisplayName}.");
        }
        catch {
            await CleanupFailedStartAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        Channel<byte[]>? audioChannel;
        Task? forwardingTask;
        IRealtimeTranscriptionSession? session;
        CancellationTokenSource? runtimeCts;

        lock (_stateLock) {
            if (!IsRunning) {
                return;
            }

            IsRunning = false;

            audioChannel = _audioChannel;
            _audioChannel = null;

            forwardingTask = _forwardingTask;
            _forwardingTask = null;

            session = _session;
            _session = null;

            runtimeCts = _runtimeCts;
            _runtimeCts = null;
        }

        _audioCaptureService.FrameCaptured -= OnFrameCaptured;

        if (audioChannel is not null) {
            audioChannel.Writer.TryComplete();
        }

        await _audioCaptureService.StopAsync();

        if (forwardingTask is not null) {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                runtimeCts?.Token ?? CancellationToken.None);

            try {
                await forwardingTask.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) {
                // Ignore cancellation during shutdown.
            }
        }

        if (session is not null) {
            session.UpdateReceived -= OnSessionUpdate;
            await session.StopAsync(cancellationToken);
            await session.DisposeAsync();
        }

        runtimeCts?.Cancel();
        runtimeCts?.Dispose();

        StatusChanged?.Invoke(this, "Live capture stopped.");
    }

    public async ValueTask DisposeAsync() {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        try {
            await StopAsync(cts.Token);
        }
        catch {
            // Ignore cleanup failures during disposal.
        }
    }

    private async Task ForwardAudioLoopAsync(CancellationToken cancellationToken) {
        Channel<byte[]>? audioChannel = _audioChannel;
        IRealtimeTranscriptionSession? session = _session;

        if (audioChannel is null || session is null) {
            return;
        }

        try {
            while (await audioChannel.Reader.WaitToReadAsync(cancellationToken)) {
                while (audioChannel.Reader.TryRead(out var pcmChunk)) {
                    await session.PushAudioAsync(pcmChunk, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) {
            // Normal stop path.
        }
        catch (Exception ex) {
            StatusChanged?.Invoke(this, $"Live forwarding failed: {ex.Message}");
        }
    }

    private void OnFrameCaptured(object? sender, AudioFrame frame) {
        if (!IsRunning) {
            return;
        }

        _audioChannel?.Writer.TryWrite(frame.Pcm16KhzMono);
    }

    private void OnSessionUpdate(object? sender, TranscriptUpdate update) {
        UpdateReceived?.Invoke(this, update);
    }

    private async Task CleanupFailedStartAsync(CancellationToken cancellationToken) {
        _audioCaptureService.FrameCaptured -= OnFrameCaptured;

        try {
            await _audioCaptureService.StopAsync();
        }
        catch {
            // Ignore cleanup failures when startup fails.
        }

        if (_audioChannel is not null) {
            _audioChannel.Writer.TryComplete();
        }

        if (_forwardingTask is not null) {
            try {
                await _forwardingTask.WaitAsync(cancellationToken);
            }
            catch {
                // Ignore cleanup failures when startup fails.
            }
        }

        if (_session is not null) {
            try {
                _session.UpdateReceived -= OnSessionUpdate;
                await _session.StopAsync(cancellationToken);
                await _session.DisposeAsync();
            }
            catch {
                // Ignore cleanup failures when startup fails.
            }
        }

        _runtimeCts?.Cancel();
        _runtimeCts?.Dispose();

        _audioChannel = null;
        _forwardingTask = null;
        _session = null;
        _runtimeCts = null;

        lock (_stateLock) {
            IsRunning = false;
        }
    }
}
