using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AudioTranscript.Abstractions;
using AudioTranscript.Audio;
using AudioTranscript.Services;
using NAudio.Wave;

namespace AudioTranscript.Engines;

public sealed class OpenAiGpt4oMiniTranscriptionEngine : ITranscriptionEngine {
    private const long MaxUploadChunkFileSizeBytes = 5L * 1024L * 1024L;
    private const long WaveHeaderSafetyBytes = 512L;

    private readonly AudioStandardizer _audioStandardizer;
    private readonly OpenAiOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ProcessLogService _processLogService;
    private readonly string _id;
    private readonly string _displayName;
    private readonly string _model;

    public OpenAiGpt4oMiniTranscriptionEngine(
        AudioStandardizer audioStandardizer,
        OpenAiOptions options,
        HttpClient httpClient,
        ProcessLogService processLogService,
        string id,
        string displayName,
        string model) {
        _audioStandardizer = audioStandardizer;
        _options = options;
        _httpClient = httpClient;
        _processLogService = processLogService;
        _id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("Engine id is required.", nameof(id)) : id.Trim();
        _displayName = string.IsNullOrWhiteSpace(displayName)
            ? throw new ArgumentException("Display name is required.", nameof(displayName))
            : displayName.Trim();
        _model = string.IsNullOrWhiteSpace(model) ? throw new ArgumentException("Model is required.", nameof(model)) : model.Trim();
    }

    public string Id => _id;

    public string DisplayName => _displayName;

    public EngineCapability Capabilities =>
        EngineCapability.Punctuation
        | EngineCapability.LanguageAutoDetect;

    public async Task<TranscriptUpdate> TranscribeFileAsync(
        string audioFilePath,
        TranscriptionRequest request,
        CancellationToken cancellationToken) {
        EnsureConfigured();
        Log($"File transcription started. Source file: '{audioFilePath}'.");

        string standardizedPath = _audioStandardizer.ConvertFileToEngineWav(audioFilePath);
        TimeSpan standardizedDuration = GetWaveDuration(standardizedPath);
        long standardizedSizeBytes = TryGetFileSizeBytes(standardizedPath);

        Log(
            $"Standardized audio ready: duration={standardizedDuration.TotalSeconds:F2}s, " +
            $"size={standardizedSizeBytes:N0} bytes, split-threshold={MaxUploadChunkFileSizeBytes:N0} bytes (5 MB).");

        try {
            string text = await RequestTranscriptWithChunkingAsync(
                standardizedPath,
                request,
                cancellationToken,
                standardizedSizeBytes);

            Log($"File transcription completed. Final transcript length: {text.Length:N0} chars.");

            return new TranscriptUpdate(
                Text: text,
                IsFinal: true,
                CreatedAt: DateTimeOffset.UtcNow,
                Language: ResolveLanguage(request));
        }
        finally {
            TryDeleteFile(standardizedPath);
            Log("Cleaned up standardized temporary audio file.");
        }
    }

    public IRealtimeTranscriptionSession CreateRealtimeSession(TranscriptionRequest request) {
        EnsureConfigured();

        return new ChunkedRealtimeTranscriptionSession(
            async (pcm16KhzMono, _, ct) => {
                byte[] wavBytes = WrapPcmAsWaveBytes(pcm16KhzMono);
                return await RequestTranscriptAsync(wavBytes, request, ct);
            },
            language: ResolveLanguage(request),
            interimWindowSeconds: 1,
            finalWindowSeconds: 3,
            interimIntervalMilliseconds: 900);
    }

    private async Task<string> RequestTranscriptWithChunkingAsync(
        string wavPath,
        TranscriptionRequest request,
        CancellationToken cancellationToken,
        long? knownSizeBytes = null) {
        long audioSizeBytes = knownSizeBytes ?? TryGetFileSizeBytes(wavPath);

        if (audioSizeBytes <= MaxUploadChunkFileSizeBytes) {
            Log(
                $"Single-pass transcription selected (size={audioSizeBytes:N0} bytes " +
                $"<= {MaxUploadChunkFileSizeBytes:N0} bytes).");

            string singleResult = await RequestTranscriptFromFileAsync(wavPath, request, cancellationToken);
            Log($"Single-pass transcription result length: {singleResult.Length:N0} chars.");
            return singleResult;
        }

        Log(
            $"Audio size {audioSizeBytes:N0} bytes exceeds split threshold " +
            $"{MaxUploadChunkFileSizeBytes:N0} bytes. Splitting into sequential chunks...");

        List<WaveChunkInfo> chunks = SplitWaveFileIntoChunks(wavPath, MaxUploadChunkFileSizeBytes);
        Log($"Created {chunks.Count} chunk(s) for file transcription.");

        var combinedTranscript = new StringBuilder();

        try {
            foreach (WaveChunkInfo chunk in chunks) {
                cancellationToken.ThrowIfCancellationRequested();

                Log(
                    $"Processing chunk {chunk.Number}/{chunks.Count}: " +
                    $"duration={chunk.Duration.TotalSeconds:F2}s, size={chunk.SizeBytes:N0} bytes.");

                var stopwatch = Stopwatch.StartNew();
                string chunkText = await RequestTranscriptFromFileAsync(chunk.Path, request, cancellationToken);
                stopwatch.Stop();

                if (string.IsNullOrWhiteSpace(chunkText)) {
                    Log($"Chunk {chunk.Number}/{chunks.Count} returned empty transcript in {stopwatch.Elapsed.TotalSeconds:F2}s.");
                    continue;
                }

                string chunkTextTrimmed = chunkText.Trim();

                if (combinedTranscript.Length > 0) {
                    combinedTranscript.AppendLine();
                }

                combinedTranscript.Append(chunkTextTrimmed);

                Log(
                    $"Chunk {chunk.Number}/{chunks.Count} completed in {stopwatch.Elapsed.TotalSeconds:F2}s; " +
                    $"transcript length={chunkTextTrimmed.Length:N0} chars.");
            }
        }
        finally {
            foreach (WaveChunkInfo chunk in chunks) {
                TryDeleteFile(chunk.Path);
            }

            Log($"Cleaned up {chunks.Count} temporary chunk file(s).");
        }

        string combined = combinedTranscript.ToString().Trim();
        Log($"Combined chunk transcripts into final text ({combined.Length:N0} chars).");
        return combined;
    }

    private async Task<string> RequestTranscriptAsync(
        byte[] wavBytes,
        TranscriptionRequest request,
        CancellationToken cancellationToken) {
        string tempWavePath = Path.Combine(Path.GetTempPath(), $"audiotranscript-openai-{Guid.NewGuid():N}.wav");

        try {
            await File.WriteAllBytesAsync(tempWavePath, wavBytes, cancellationToken);
            return await RequestTranscriptFromFileAsync(tempWavePath, request, cancellationToken);
        }
        finally {
            TryDeleteFile(tempWavePath);
        }
    }

    private async Task<string> RequestTranscriptFromFileAsync(
        string wavPath,
        TranscriptionRequest request,
        CancellationToken cancellationToken) {
        string model = ResolveModel();
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(model), "model");
        multipart.Add(new StringContent("json"), "response_format");

        string language = ResolveLanguage(request);
        string prompt = ResolvePrompt(request);

        if (!string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)) {
            multipart.Add(new StringContent(language), "language");
        }

        if (!string.IsNullOrWhiteSpace(prompt)) {
            multipart.Add(new StringContent(prompt), "prompt");
        }

        long requestFileSize = TryGetFileSizeBytes(wavPath);
        Log(
            $"Submitting OpenAI request for '{Path.GetFileName(wavPath)}' " +
            $"({requestFileSize:N0} bytes), model='{model}', language='{language}', prompt={(string.IsNullOrWhiteSpace(prompt) ? "none" : "set")}.");

        await using var fileStream = new FileStream(
            wavPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        var audioContent = new StreamContent(fileStream);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        multipart.Add(audioContent, "file", Path.GetFileName(wavPath));

        message.Content = multipart;

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(_options.TimeoutSeconds, 10)));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        HttpResponseMessage response;
        try {
            response = await _httpClient.SendAsync(message, linkedCts.Token);
        }
        catch (HttpRequestException ex) {
            string connectivityMessage = BuildOpenAiConnectivityMessage(ex);
            Log(connectivityMessage);
            throw new InvalidOperationException(connectivityMessage, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested) {
            Log("OpenAI request timed out.");
            throw new InvalidOperationException(
                "OpenAI request timed out. Check your internet connection and try again.",
                ex);
        }

        using (response) {
            string body = await response.Content.ReadAsStringAsync(linkedCts.Token);

            if (!response.IsSuccessStatusCode) {
                string errorMessage = ExtractOpenAiError(body);
                string suffix = string.IsNullOrWhiteSpace(errorMessage) ? string.Empty : $" Error: {errorMessage}";
                Log(
                    $"OpenAI request failed ({(int)response.StatusCode} {response.ReasonPhrase}). " +
                    $"{errorMessage}".Trim());

                throw new InvalidOperationException(
                    $"OpenAI request failed ({(int)response.StatusCode} {response.ReasonPhrase}).{suffix}");
            }

            string text = ExtractTranscript(body);
            Log(
                $"OpenAI request succeeded for '{Path.GetFileName(wavPath)}'; " +
                $"received {text.Trim().Length:N0} chars.");
            return text.Trim();
        }
    }

    private static string ResolveLanguage(TranscriptionRequest request) {
        string language = request.Language?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(language) ? "auto" : language;
    }

    private static string ResolvePrompt(TranscriptionRequest request) {
        return request.Prompt?.Trim() ?? string.Empty;
    }

    private void EnsureConfigured() {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)) {
            throw new InvalidOperationException(
                "OpenAI API key is missing. Set OPENAI_API_KEY or update settings in-app.");
        }
    }

    private string ResolveModel() {
        return _model;
    }

    private static string ExtractTranscript(string responseBody) {
        if (string.IsNullOrWhiteSpace(responseBody)) {
            return string.Empty;
        }

        try {
            using var document = JsonDocument.Parse(responseBody);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("text", out var textNode)
                && textNode.ValueKind == JsonValueKind.String) {
                return textNode.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("output_text", out var outputText)
                && outputText.ValueKind == JsonValueKind.String) {
                return outputText.GetString() ?? string.Empty;
            }
        }
        catch (JsonException) {
            // Some response formats can return plain text.
            return responseBody;
        }

        return string.Empty;
    }

    private static string ExtractOpenAiError(string responseBody) {
        if (string.IsNullOrWhiteSpace(responseBody)) {
            return string.Empty;
        }

        try {
            using var document = JsonDocument.Parse(responseBody);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("error", out JsonElement errorNode)
                && errorNode.ValueKind == JsonValueKind.Object
                && errorNode.TryGetProperty("message", out JsonElement messageNode)
                && messageNode.ValueKind == JsonValueKind.String) {
                return messageNode.GetString() ?? string.Empty;
            }
        }
        catch (JsonException) {
            return string.Empty;
        }

        return string.Empty;
    }

    private static string BuildOpenAiConnectivityMessage(HttpRequestException exception) {
        if (IsDnsResolutionFailure(exception)) {
            return "Unable to reach OpenAI because DNS could not resolve api.openai.com. " +
                   "Check your internet connection, DNS, VPN/proxy, or firewall and try again.";
        }

        return $"Unable to reach OpenAI service: {exception.Message}";
    }

    private static bool IsDnsResolutionFailure(HttpRequestException exception) {
        if (exception.InnerException is SocketException socketException
            && (socketException.SocketErrorCode == SocketError.HostNotFound
                || socketException.SocketErrorCode == SocketError.NoData
                || socketException.SocketErrorCode == SocketError.TryAgain)) {
            return true;
        }

        return exception.Message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan GetWaveDuration(string wavPath) {
        using var reader = new WaveFileReader(wavPath);
        return reader.TotalTime;
    }

    private List<WaveChunkInfo> SplitWaveFileIntoChunks(string wavPath, long maxChunkFileSizeBytes) {
        using var reader = new WaveFileReader(wavPath);

        WaveFormat format = reader.WaveFormat;
        int bytesPerSecond = Math.Max(format.AverageBytesPerSecond, 1);
        int blockAlign = Math.Max(format.BlockAlign, 1);

        long chunkSizeBytes = Math.Max(maxChunkFileSizeBytes - WaveHeaderSafetyBytes, blockAlign);
        chunkSizeBytes -= chunkSizeBytes % blockAlign;
        chunkSizeBytes = Math.Max(chunkSizeBytes, blockAlign);

        var chunks = new List<WaveChunkInfo>();
        byte[] buffer = new byte[81920];
        int chunkIndex = 0;

        while (reader.Position < reader.Length) {
            string chunkPath = Path.Combine(
                Path.GetTempPath(),
                $"audiotranscript-openai-chunk-{Guid.NewGuid():N}-{chunkIndex:D4}.wav");

            long written = 0;
            using (var writer = new WaveFileWriter(chunkPath, format)) {
                while (written < chunkSizeBytes && reader.Position < reader.Length) {
                    int bytesRemaining = (int)Math.Min(buffer.Length, chunkSizeBytes - written);
                    int read = reader.Read(buffer, 0, bytesRemaining);

                    if (read <= 0) {
                        break;
                    }

                    writer.Write(buffer, 0, read);
                    written += read;
                }
            }

            TimeSpan chunkAudioDuration = TimeSpan.FromSeconds(written / (double)bytesPerSecond);
            long chunkFileSizeBytes = TryGetFileSizeBytes(chunkPath);
            var info = new WaveChunkInfo(chunkIndex + 1, chunkPath, chunkFileSizeBytes, chunkAudioDuration);
            chunks.Add(info);

            Log(
                $"Prepared chunk {info.Number}: duration={info.Duration.TotalSeconds:F2}s, " +
                $"size={info.SizeBytes:N0} bytes (limit {MaxUploadChunkFileSizeBytes:N0}).");

            chunkIndex++;
        }

        return chunks;
    }

    private static byte[] WrapPcmAsWaveBytes(byte[] pcm16KhzMono) {
        string tempWavePath = Path.Combine(Path.GetTempPath(), $"audiotranscript-online-{Guid.NewGuid():N}.wav");

        try {
            using (var writer = new WaveFileWriter(tempWavePath, AudioFormatConstants.EngineWaveFormat)) {
                writer.Write(pcm16KhzMono, 0, pcm16KhzMono.Length);
            }

            return File.ReadAllBytes(tempWavePath);
        }
        finally {
            TryDeleteFile(tempWavePath);
        }
    }

    private static void TryDeleteFile(string filePath) {
        try {
            if (File.Exists(filePath)) {
                File.Delete(filePath);
            }
        }
        catch {
            // Ignore temporary cleanup failures.
        }
    }

    private static long TryGetFileSizeBytes(string filePath) {
        try {
            return new FileInfo(filePath).Length;
        }
        catch {
            return 0;
        }
    }

    private void Log(string message) {
        _processLogService.Log("OpenAI", message);
    }

    private sealed record WaveChunkInfo(
        int Number,
        string Path,
        long SizeBytes,
        TimeSpan Duration
    );
}
