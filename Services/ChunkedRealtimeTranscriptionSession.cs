using System.Threading.Channels;
using AudioTranscript.Abstractions;
using AudioTranscript.Audio;

namespace AudioTranscript.Services;

public sealed class ChunkedRealtimeTranscriptionSession : IRealtimeTranscriptionSession {
    private readonly Func<byte[], bool, CancellationToken, Task<TranscriptionResult>> _transcribeChunkAsync;
    private readonly Channel<byte[]> _channel;
    private readonly object _bufferLock = new();
    private readonly List<byte> _buffer = new();
    private readonly int _interimWindowBytes;
    private readonly int _finalWindowBytes;
    private readonly TimeSpan _interimInterval;
    private readonly string? _language;
    private CancellationTokenSource? _lifetimeCts;
    private Task? _processingTask;
    private long _finalizedBytes;
    private DateTimeOffset _lastInterimAt = DateTimeOffset.MinValue;
    private bool _started;

    public ChunkedRealtimeTranscriptionSession(
        Func<byte[], bool, CancellationToken, Task<TranscriptionResult>> transcribeChunkAsync,
        string? language,
        int interimWindowSeconds = 2,
        int finalWindowSeconds = 5,
        int interimIntervalMilliseconds = 1300) {
        _transcribeChunkAsync = transcribeChunkAsync;
        _language = language;
        _channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions {
            SingleReader = true,
            SingleWriter = false,
        });
        _interimWindowBytes = AudioFormatConstants.BytesPerSecond * Math.Max(interimWindowSeconds, 1);
        _finalWindowBytes = AudioFormatConstants.BytesPerSecond * Math.Max(finalWindowSeconds, 2);
        _interimInterval = TimeSpan.FromMilliseconds(Math.Max(interimIntervalMilliseconds, 300));
    }

    public event EventHandler<TranscriptUpdate>? UpdateReceived;

    public Task StartAsync(CancellationToken cancellationToken) {
        if (_started) {
            return Task.CompletedTask;
        }

        _started = true;
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = Task.Run(() => ProcessLoopAsync(_lifetimeCts.Token), _lifetimeCts.Token);
        return Task.CompletedTask;
    }

    public async Task PushAudioAsync(ReadOnlyMemory<byte> pcm16KhzMono, CancellationToken cancellationToken) {
        if (!_started) {
            throw new InvalidOperationException("The session has not been started.");
        }

        if (pcm16KhzMono.IsEmpty) {
            return;
        }

        await _channel.Writer.WriteAsync(pcm16KhzMono.ToArray(), cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        _channel.Writer.TryComplete();

        if (_processingTask is not null) {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCts?.Token ?? CancellationToken.None);

            await _processingTask.WaitAsync(linked.Token);
        }

        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
        _started = false;
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

    private async Task ProcessLoopAsync(CancellationToken cancellationToken) {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken)) {
            while (_channel.Reader.TryRead(out byte[]? chunk)) {
                lock (_bufferLock) {
                    _buffer.AddRange(chunk);
                }

                await EmitFinalChunksIfReadyAsync(cancellationToken);
                await EmitInterimIfReadyAsync(cancellationToken);
            }
        }

        await FlushRemainingAsync(cancellationToken);
    }

    private async Task EmitFinalChunksIfReadyAsync(CancellationToken cancellationToken) {
        while (true) {
            byte[] finalChunk;

            lock (_bufferLock) {
                if (_buffer.Count < _finalWindowBytes) {
                    return;
                }

                finalChunk = _buffer.Take(_finalWindowBytes).ToArray();
                _buffer.RemoveRange(0, _finalWindowBytes);
            }

            await EmitUpdateAsync(finalChunk, isFinal: true, cancellationToken);
            _finalizedBytes += finalChunk.Length;
        }
    }

    private async Task EmitInterimIfReadyAsync(CancellationToken cancellationToken) {
        if (DateTimeOffset.UtcNow - _lastInterimAt < _interimInterval) {
            return;
        }

        byte[] snapshot;

        lock (_bufferLock) {
            if (_buffer.Count < _interimWindowBytes) {
                return;
            }

            snapshot = _buffer.ToArray();
        }

        await EmitUpdateAsync(snapshot, isFinal: false, cancellationToken);
        _lastInterimAt = DateTimeOffset.UtcNow;
    }

    private async Task FlushRemainingAsync(CancellationToken cancellationToken) {
        byte[] remainder;

        lock (_bufferLock) {
            if (_buffer.Count == 0) {
                return;
            }

            remainder = _buffer.ToArray();
            _buffer.Clear();
        }

        await EmitUpdateAsync(remainder, isFinal: true, cancellationToken);
        _finalizedBytes += remainder.Length;
    }

    private async Task EmitUpdateAsync(byte[] pcmChunk, bool isFinal, CancellationToken cancellationToken) {
        TranscriptionResult result = await _transcribeChunkAsync(pcmChunk, isFinal, cancellationToken);

        if (string.IsNullOrWhiteSpace(result.Text)) {
            return;
        }

        TimeSpan segmentStart = TimeSpan.FromSeconds(_finalizedBytes / (double)AudioFormatConstants.BytesPerSecond);
        TimeSpan segmentEnd = TimeSpan.FromSeconds(
            (_finalizedBytes + pcmChunk.Length) / (double)AudioFormatConstants.BytesPerSecond);

        UpdateReceived?.Invoke(
            this,
            new TranscriptUpdate(
                Text: result.Text.Trim(),
                IsFinal: isFinal,
                CreatedAt: DateTimeOffset.UtcNow,
                SegmentStart: segmentStart,
                SegmentEnd: segmentEnd,
                Language: _language,
                TokenLogprobs: result.TokenLogprobs,
                LowConfidenceTokens: result.LowConfidenceTokens));
    }
}
