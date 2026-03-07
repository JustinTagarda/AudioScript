using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AudioTranscript.Abstractions;
using AudioTranscript.Audio;
using NAudio.Wave;

namespace AudioTranscript.Services;

public sealed class OpenAiAudioTranscriptionService : ITranscriptionService {
    private const long MaxUploadBytes = 25L * 1024L * 1024L;
    private const long ChunkWaveHeaderSafetyBytes = 1024L;
    private const long ChunkTargetBytes = MaxUploadBytes - ChunkWaveHeaderSafetyBytes;

    private const string ResponseFormat = "json";
    private const string Temperature = "0";
    private const string ChunkingStrategy = "auto";
    private const string IncludeLogprobs = "logprobs";
    private const string StreamDisabled = "false";

    private readonly AudioStandardizer _audioStandardizer;
    private readonly HttpClient _httpClient;
    private readonly OpenAiTranscriptionOptions _options;
    private readonly ProcessLogService _processLogService;
    private readonly OpenAiTranscriptionResponseParser _responseParser;

    public OpenAiAudioTranscriptionService(
        AudioStandardizer audioStandardizer,
        HttpClient httpClient,
        OpenAiTranscriptionOptions options,
        ProcessLogService processLogService,
        OpenAiTranscriptionResponseParser responseParser) {
        _audioStandardizer = audioStandardizer;
        _httpClient = httpClient;
        _options = options;
        _processLogService = processLogService;
        _responseParser = responseParser;
    }

    public async Task<TranscriptionResult> TranscribeFileAsync(
        string audioFilePath,
        string model,
        CancellationToken cancellationToken) {
        string validatedModel = ValidateModel(model);
        string validatedPath = ValidateAndResolveFilePath(audioFilePath);
        var info = new FileInfo(validatedPath);

        if (info.Length <= 0) {
            throw new InvalidOperationException("Audio file is empty.");
        }

        Log(
            $"Transcription request started for file '{info.Name}' (ext='{info.Extension}', " +
            $"size={info.Length:N0} bytes) using model '{validatedModel}'.");

        if (info.Length >= MaxUploadBytes) {
            Log(
                $"File size is >= 25 MB ({info.Length:N0} bytes). " +
                "Splitting into chunks before transcription.");
            return await TranscribeFileWithChunkingAsync(validatedPath, validatedModel, cancellationToken);
        }

        try {
            await using var stream = new FileStream(
                validatedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);

            return await SendRequestAsync(
                audioStream: stream,
                fileName: info.Name,
                mediaType: ResolveMediaType(info.Extension),
                fileSizeBytes: info.Length,
                model: validatedModel,
                cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException ex) when (LooksLikeDurationLimitError(ex)) {
            Log(
                "Transcription failed due to model duration limit. " +
                "Retrying with chunked transcription.");
            return await TranscribeFileWithChunkingAsync(validatedPath, validatedModel, cancellationToken);
        }
    }

    private async Task<TranscriptionResult> TranscribeFileWithChunkingAsync(
        string sourcePath,
        string model,
        CancellationToken cancellationToken) {
        string standardizedWavePath = _audioStandardizer.ConvertFileToEngineWav(sourcePath);
        List<WaveChunkInfo> chunks = SplitWaveFileIntoChunks(standardizedWavePath, ChunkTargetBytes);

        if (chunks.Count == 0) {
            throw new InvalidOperationException("Unable to split audio file into transcription chunks.");
        }

        Log($"Prepared {chunks.Count} chunk(s) for transcription.");

        var textBuilder = new StringBuilder();
        var combinedLogprobs = new List<TranscriptionTokenLogprob>();
        var combinedLowConfidence = new List<LowConfidenceToken>();
        var combinedTimedLines = new List<TranscriptionTimedLine>();
        TimeSpan totalDuration = TimeSpan.Zero;
        int tokenIndex = 0;

        try {
            foreach (WaveChunkInfo chunk in chunks) {
                cancellationToken.ThrowIfCancellationRequested();

                Log(
                    $"Transcribing chunk {chunk.Number}/{chunks.Count} " +
                    $"(size={chunk.SizeBytes:N0} bytes, duration={chunk.Duration.TotalSeconds:F2}s).");

                await using var chunkStream = new FileStream(
                    chunk.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 81920,
                    useAsync: true);

                TranscriptionResult chunkResult = await SendRequestAsync(
                    audioStream: chunkStream,
                    fileName: Path.GetFileName(chunk.Path),
                    mediaType: "audio/wav",
                    fileSizeBytes: chunk.SizeBytes,
                    model: model,
                    cancellationToken: cancellationToken);

                TimeSpan chunkStart = totalDuration;
                TimeSpan chunkDuration = chunkResult.Duration ?? chunk.Duration;

                if (!string.IsNullOrWhiteSpace(chunkResult.Text)) {
                    if (textBuilder.Length > 0) {
                        textBuilder.AppendLine();
                    }

                    textBuilder.Append(chunkResult.Text.Trim());
                }

                IReadOnlyList<TranscriptionTimedLine> chunkLines =
                    chunkResult.TimedLines is not null && chunkResult.TimedLines.Count > 0
                        ? chunkResult.TimedLines
                        : BuildTimedLines(chunkResult.Text, TimeSpan.Zero, chunkDuration);

                foreach (TranscriptionTimedLine line in chunkLines) {
                    combinedTimedLines.Add(
                        new TranscriptionTimedLine(
                            line.Text,
                            chunkStart + line.StartOffset));
                }

                foreach (TranscriptionTokenLogprob item in chunkResult.TokenLogprobs) {
                    var reindexed = new TranscriptionTokenLogprob(item.Token, item.Logprob, tokenIndex);
                    combinedLogprobs.Add(reindexed);

                    if (item.Logprob <= _options.LowConfidenceLogprobThreshold) {
                        combinedLowConfidence.Add(new LowConfidenceToken(item.Token, item.Logprob, tokenIndex));
                    }

                    tokenIndex++;
                }

                totalDuration += chunkDuration;
            }
        }
        finally {
            TryDeleteFile(standardizedWavePath);

            foreach (WaveChunkInfo chunk in chunks) {
                TryDeleteFile(chunk.Path);
            }
        }

        return new TranscriptionResult(
            Text: textBuilder.ToString().Trim(),
            Model: model,
            CreatedAt: DateTimeOffset.UtcNow,
            Duration: totalDuration == TimeSpan.Zero ? null : totalDuration,
            TokenLogprobs: combinedLogprobs,
            LowConfidenceTokens: combinedLowConfidence,
            TimedLines: combinedTimedLines);
    }

    private async Task<TranscriptionResult> SendRequestAsync(
        Stream audioStream,
        string fileName,
        string mediaType,
        long fileSizeBytes,
        string model,
        CancellationToken cancellationToken) {
        EnsureConfigured();
        audioStream.Position = 0;

        var stopwatch = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        Log(
            $"Submitting transcription request for '{fileName}' " +
            $"({fileSizeBytes:N0} bytes) using model '{model}'.");

        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(model), "model");
        multipart.Add(new StringContent(ResponseFormat), "response_format");
        multipart.Add(new StringContent(Temperature), "temperature");
        multipart.Add(new StringContent(ChunkingStrategy), "chunking_strategy");
        multipart.Add(new StringContent(IncludeLogprobs), "include[]");
        multipart.Add(new StringContent(StreamDisabled), "stream");
        multipart.Add(new StringContent(_options.Prompt.Trim()), "prompt");

        var audioContent = new StreamContent(audioStream);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        multipart.Add(audioContent, "file", fileName);

        request.Content = multipart;

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(_options.TimeoutSeconds, 30)));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        HttpResponseMessage response;

        try {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linkedCts.Token);
        }
        catch (TaskCanceledException ex) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested) {
            throw new TimeoutException("OpenAI transcription request timed out.", ex);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (HttpRequestException ex) {
            throw new InvalidOperationException(BuildConnectivityMessage(ex), ex);
        }

        using (response) {
            string responseBody = await response.Content.ReadAsStringAsync(linkedCts.Token);

            if (!response.IsSuccessStatusCode) {
                string apiErrorMessage = ExtractApiErrorMessage(responseBody);
                string message = string.IsNullOrWhiteSpace(apiErrorMessage)
                    ? $"OpenAI transcription request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"OpenAI transcription request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {apiErrorMessage}";

                Log(
                    $"Transcription request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}) " +
                    $"for '{fileName}' ({fileSizeBytes:N0} bytes).");

                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden) {
                    throw new UnauthorizedAccessException(message);
                }

                throw new InvalidOperationException(message);
            }

            TranscriptionResult result;

            try {
                result = _responseParser.Parse(
                    responseBody,
                    model,
                    _options.LowConfidenceLogprobThreshold);

                IReadOnlyList<TranscriptionTimedLine> timedLines = BuildTimedLines(
                    result.Text,
                    TimeSpan.Zero,
                    result.Duration);

                result = result with {
                    TimedLines = timedLines,
                };
            }
            catch (Exception ex) {
                Log($"Transcription response parsing failed for '{fileName}': {ex.Message}");
                throw;
            }

            stopwatch.Stop();
            Log(
                $"Transcription request completed for '{fileName}' in {stopwatch.Elapsed.TotalSeconds:F2}s. " +
                $"chars={result.Text.Length:N0}, logprobs={result.TokenLogprobs.Count:N0}, " +
                $"low-confidence={result.LowConfidenceTokens.Count:N0}.");

            return result;
        }
    }

    private List<WaveChunkInfo> SplitWaveFileIntoChunks(string wavePath, long maxChunkFileSizeBytes) {
        using var reader = new WaveFileReader(wavePath);

        WaveFormat format = reader.WaveFormat;
        int blockAlign = Math.Max(format.BlockAlign, 1);
        int bytesPerSecond = Math.Max(format.AverageBytesPerSecond, 1);

        long chunkDataBytes = Math.Max(maxChunkFileSizeBytes - ChunkWaveHeaderSafetyBytes, blockAlign);
        chunkDataBytes -= chunkDataBytes % blockAlign;
        chunkDataBytes = Math.Max(chunkDataBytes, blockAlign);

        var chunks = new List<WaveChunkInfo>();
        byte[] buffer = new byte[81920];
        int chunkIndex = 0;

        while (reader.Position < reader.Length) {
            string chunkPath = Path.Combine(
                Path.GetTempPath(),
                $"audiotranscript-chunk-{Guid.NewGuid():N}-{chunkIndex:D4}.wav");

            long written = 0;

            using (var writer = new WaveFileWriter(chunkPath, format)) {
                while (written < chunkDataBytes && reader.Position < reader.Length) {
                    int bytesToRead = (int)Math.Min(buffer.Length, chunkDataBytes - written);
                    int read = reader.Read(buffer, 0, bytesToRead);

                    if (read <= 0) {
                        break;
                    }

                    writer.Write(buffer, 0, read);
                    written += read;
                }
            }

            if (written <= 0) {
                TryDeleteFile(chunkPath);
                break;
            }

            long chunkSize = TryGetFileSize(chunkPath);
            TimeSpan chunkDuration = TimeSpan.FromSeconds(written / (double)bytesPerSecond);
            chunks.Add(new WaveChunkInfo(
                Number: chunkIndex + 1,
                Path: chunkPath,
                SizeBytes: chunkSize,
                Duration: chunkDuration));
            chunkIndex++;
        }

        return chunks;
    }

    private void EnsureConfigured() {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)) {
            throw new InvalidOperationException("OpenAI API key is required for transcription.");
        }

        if (_options.Endpoint is null) {
            throw new InvalidOperationException("OpenAI transcription endpoint is not configured.");
        }
    }

    private static string ValidateAndResolveFilePath(string audioFilePath) {
        if (string.IsNullOrWhiteSpace(audioFilePath)) {
            throw new ArgumentException("Audio file path is required.", nameof(audioFilePath));
        }

        string fullPath = Path.GetFullPath(audioFilePath.Trim());

        if (!File.Exists(fullPath)) {
            throw new FileNotFoundException("Audio file was not found.", fullPath);
        }

        try {
            using FileStream stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) {
            throw new IOException($"Audio file cannot be accessed: {ex.Message}", ex);
        }

        return fullPath;
    }

    private static string ValidateModel(string model) {
        string trimmed = model?.Trim() ?? string.Empty;

        if (!OpenAiTranscriptionModelCatalog.IsSupported(trimmed)) {
            throw new InvalidOperationException(
                $"Unsupported transcription model '{model}'. " +
                $"Use '{OpenAiTranscriptionModelCatalog.Gpt4oTranscribe}' or '{OpenAiTranscriptionModelCatalog.Gpt4oMiniTranscribe}'.");
        }

        return trimmed;
    }

    private static string ResolveMediaType(string extension) {
        string normalized = extension?.Trim().ToLowerInvariant() ?? string.Empty;

        return normalized switch {
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".mp4" => "audio/mp4",
            ".aac" => "audio/aac",
            ".flac" => "audio/flac",
            ".ogg" => "audio/ogg",
            ".wma" => "audio/x-ms-wma",
            _ => "application/octet-stream",
        };
    }

    private static string ExtractApiErrorMessage(string responseBody) {
        if (string.IsNullOrWhiteSpace(responseBody)) {
            return string.Empty;
        }

        try {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("error", out JsonElement errorNode)
                && errorNode.ValueKind == JsonValueKind.Object
                && errorNode.TryGetProperty("message", out JsonElement messageNode)
                && messageNode.ValueKind == JsonValueKind.String) {
                return messageNode.GetString() ?? string.Empty;
            }
        }
        catch {
            // Ignore malformed API error payloads.
        }

        return string.Empty;
    }

    private static string BuildConnectivityMessage(HttpRequestException exception) {
        if (exception.InnerException is SocketException socketException
            && (socketException.SocketErrorCode == SocketError.HostNotFound
                || socketException.SocketErrorCode == SocketError.NoData
                || socketException.SocketErrorCode == SocketError.TryAgain)) {
            return "Unable to reach OpenAI because DNS could not resolve api.openai.com.";
        }

        return $"Unable to reach OpenAI transcription service: {exception.Message}";
    }

    private static IReadOnlyList<TranscriptionTimedLine> BuildTimedLines(
        string text,
        TimeSpan startOffset,
        TimeSpan? duration) {
        if (string.IsNullOrWhiteSpace(text)) {
            return Array.Empty<TranscriptionTimedLine>();
        }

        string[] parts = text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        if (parts.Length == 0) {
            return Array.Empty<TranscriptionTimedLine>();
        }

        var lines = new List<TranscriptionTimedLine>(parts.Length);
        double totalSeconds = Math.Max(duration?.TotalSeconds ?? 0, 0);
        double stepSeconds = parts.Length > 1 && totalSeconds > 0
            ? totalSeconds / parts.Length
            : 0;

        for (int index = 0; index < parts.Length; index++) {
            TimeSpan offset = startOffset + TimeSpan.FromSeconds(stepSeconds * index);
            lines.Add(new TranscriptionTimedLine(parts[index], offset));
        }

        return lines;
    }

    private void Log(string message) {
        _processLogService.Log("OpenAI", message);
    }

    private static bool LooksLikeDurationLimitError(Exception ex) {
        string message = ex.Message ?? string.Empty;

        return message.Contains("audio duration", StringComparison.OrdinalIgnoreCase)
            && message.Contains("longer than", StringComparison.OrdinalIgnoreCase)
            && message.Contains("maximum", StringComparison.OrdinalIgnoreCase);
    }

    private static long TryGetFileSize(string filePath) {
        try {
            return new FileInfo(filePath).Length;
        }
        catch {
            return 0;
        }
    }

    private static void TryDeleteFile(string filePath) {
        try {
            if (File.Exists(filePath)) {
                File.Delete(filePath);
            }
        }
        catch {
            // Best-effort cleanup.
        }
    }

    private sealed record WaveChunkInfo(
        int Number,
        string Path,
        long SizeBytes,
        TimeSpan Duration
    );
}
