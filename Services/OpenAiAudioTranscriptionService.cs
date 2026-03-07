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
    private const string StandardResponseFormat = "json";
    private const string TimestampedResponseFormat = "verbose_json";
    private const string Whisper1Model = "whisper-1";
    private const string Temperature = "0";
    private const string ChunkingStrategy = "auto";
    private const string IncludeLogprobs = "logprobs";
    private const string StreamDisabled = "false";
    private const string TimestampGranularitySegment = "segment";

    private readonly AudioStandardizer _audioStandardizer;
    private readonly AudioChunkPlanner _audioChunkPlanner;
    private readonly TranscriptionSegmentMerger _segmentMerger;
    private readonly HttpClient _httpClient;
    private readonly OpenAiTranscriptionOptions _options;
    private readonly ProcessLogService _processLogService;
    private readonly OpenAiTranscriptionResponseParser _responseParser;

    public OpenAiAudioTranscriptionService(
        AudioStandardizer audioStandardizer,
        AudioChunkPlanner audioChunkPlanner,
        TranscriptionSegmentMerger segmentMerger,
        HttpClient httpClient,
        OpenAiTranscriptionOptions options,
        ProcessLogService processLogService,
        OpenAiTranscriptionResponseParser responseParser) {
        _audioStandardizer = audioStandardizer;
        _audioChunkPlanner = audioChunkPlanner;
        _segmentMerger = segmentMerger;
        _httpClient = httpClient;
        _options = options;
        _processLogService = processLogService;
        _responseParser = responseParser;
    }

    public async Task<TranscriptionResult> TranscribeFileAsync(
        string audioFilePath,
        string model,
        CancellationToken cancellationToken,
        IProgress<TranscriptionProgressUpdate>? progress = null) {
        string validatedModel = ValidateModel(model);
        string validatedPath = ValidateAndResolveFilePath(audioFilePath);
        var info = new FileInfo(validatedPath);

        if (info.Length <= 0) {
            throw new InvalidOperationException("Audio file is empty.");
        }

        Log(
            $"Transcription request started for file '{info.Name}' (ext='{info.Extension}', " +
            $"size={info.Length:N0} bytes) using model '{validatedModel}'.");

        if (info.Length >= AudioChunkPlanner.MaxUploadBytes) {
            Log(
                $"File size is >= 25 MB ({info.Length:N0} bytes). " +
                "Transcribing in multiple chunks with absolute timeline merge.");
            progress?.Report(new TranscriptionProgressUpdate(
                StatusMessage: "Large file detected. It will be transcribed in multiple parts automatically.",
                IsLargeFile: true));

            return await TranscribeFileWithChunkingAsync(
                validatedPath,
                validatedModel,
                cancellationToken,
                progress,
                announceLargeFile: true);
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
                requestMode: TranscriptionRequestMode.Standard,
                cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException ex) when (LooksLikeDurationLimitError(ex)) {
            Log(
                "Transcription failed due to model duration limit. " +
                "Retrying with chunked transcription.");

            return await TranscribeFileWithChunkingAsync(
                validatedPath,
                validatedModel,
                cancellationToken,
                progress,
                announceLargeFile: false);
        }
    }

    private async Task<TranscriptionResult> TranscribeFileWithChunkingAsync(
        string sourcePath,
        string model,
        CancellationToken cancellationToken,
        IProgress<TranscriptionProgressUpdate>? progress,
        bool announceLargeFile) {
        bool supportsTimestampedChunkResponses = SupportsTimestampedChunkResponses(model);

        progress?.Report(new TranscriptionProgressUpdate(
            StatusMessage: "Preparing audio for multi-part transcription...",
            IsLargeFile: announceLargeFile));

        if (!supportsTimestampedChunkResponses) {
            Log(
                $"Model '{model}' does not support timestamped chunk responses on the transcription API. " +
                "Falling back to JSON chunk responses with estimated merged timeline output.");
        }

        string standardizedWavePath = _audioStandardizer.ConvertFileToEngineWav(sourcePath);
        List<PreparedWaveChunk> chunks;
        TimeSpan totalDuration;

        try {
            chunks = CreateWaveChunks(
                standardizedWavePath,
                supportsTimestampedChunkResponses
                    ? null
                    : TimeSpan.Zero);

            using var durationReader = new WaveFileReader(standardizedWavePath);
            totalDuration = durationReader.TotalTime;
        }
        catch {
            TryDeleteFile(standardizedWavePath);
            throw;
        }

        if (chunks.Count == 0) {
            TryDeleteFile(standardizedWavePath);
            throw new InvalidOperationException("Unable to split audio file into transcription chunks.");
        }

        Log($"Prepared {chunks.Count} chunk(s) for transcription.");

        var combinedLogprobs = new List<TranscriptionTokenLogprob>();
        var combinedLowConfidence = new List<LowConfidenceToken>();
        var absoluteSegments = new List<ChunkTranscriptionSegment>();
        var mergedChunkTexts = new List<string>();
        int tokenIndex = 0;

        try {
            foreach (PreparedWaveChunk chunk in chunks) {
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report(new TranscriptionProgressUpdate(
                    StatusMessage: $"Transcribing chunk {chunk.Plan.ChunkIndex + 1} of {chunks.Count}...",
                    IsLargeFile: announceLargeFile,
                    ChunkIndex: chunk.Plan.ChunkIndex + 1,
                    TotalChunks: chunks.Count));

                Log(
                    $"Transcribing chunk {chunk.Plan.ChunkIndex + 1}/{chunks.Count} " +
                    $"(size={chunk.SizeBytes:N0} bytes, start={chunk.Plan.StartOffset.TotalSeconds:F2}s, " +
                    $"duration={chunk.Plan.Duration.TotalSeconds:F2}s).");

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
                    requestMode: supportsTimestampedChunkResponses
                        ? TranscriptionRequestMode.TimestampedChunk
                        : TranscriptionRequestMode.Standard,
                    cancellationToken: cancellationToken);

                if (supportsTimestampedChunkResponses) {
                    if (chunkResult.TimedLines is null || chunkResult.TimedLines.Count == 0) {
                        throw new InvalidOperationException(
                            "Chunk transcription did not return timestamped segments needed to merge the final transcript accurately.");
                    }

                    foreach (TranscriptionTimedLine line in chunkResult.TimedLines.Where(line => !string.IsNullOrWhiteSpace(line.Text))) {
                        TimeSpan localEnd = line.EndOffset ?? line.StartOffset;
                        absoluteSegments.Add(new ChunkTranscriptionSegment(
                            ChunkIndex: chunk.Plan.ChunkIndex,
                            ChunkStartOffset: chunk.Plan.StartOffset,
                            LocalStartOffset: line.StartOffset,
                            LocalEndOffset: localEnd,
                            Text: line.Text));
                    }
                }
                else if (!string.IsNullOrWhiteSpace(chunkResult.Text)) {
                    mergedChunkTexts.Add(chunkResult.Text.Trim());
                }

                foreach (TranscriptionTokenLogprob item in chunkResult.TokenLogprobs) {
                    var reindexed = new TranscriptionTokenLogprob(item.Token, item.Logprob, tokenIndex);
                    combinedLogprobs.Add(reindexed);

                    if (item.Logprob <= _options.LowConfidenceLogprobThreshold) {
                        combinedLowConfidence.Add(new LowConfidenceToken(item.Token, item.Logprob, tokenIndex));
                    }

                    tokenIndex++;
                }
            }
        }
        finally {
            TryDeleteFile(standardizedWavePath);

            foreach (PreparedWaveChunk chunk in chunks) {
                TryDeleteFile(chunk.Path);
            }
        }

        progress?.Report(new TranscriptionProgressUpdate(
            StatusMessage: supportsTimestampedChunkResponses
                ? "Merging chunk timelines..."
                : "Merging chunk transcript...",
            IsLargeFile: announceLargeFile,
            ChunkIndex: chunks.Count,
            TotalChunks: chunks.Count));

        if (!supportsTimestampedChunkResponses) {
            string mergedText = string.Join(
                Environment.NewLine,
                mergedChunkTexts.Where(text => !string.IsNullOrWhiteSpace(text)));

            return new TranscriptionResult(
                Text: mergedText,
                Model: model,
                CreatedAt: DateTimeOffset.UtcNow,
                Duration: totalDuration > TimeSpan.Zero ? totalDuration : null,
                TokenLogprobs: combinedLogprobs,
                LowConfidenceTokens: combinedLowConfidence,
                TimedLines: Array.Empty<TranscriptionTimedLine>());
        }

        IReadOnlyList<TranscriptionTimedLine> mergedTimedLines = _segmentMerger.Merge(absoluteSegments);

        if (mergedTimedLines.Count == 0) {
            throw new InvalidOperationException(
                "Large-file chunk transcription completed but did not produce a merged transcript timeline.");
        }

        string mergedTimedText = string.Join(
            Environment.NewLine,
            mergedTimedLines
                .Select(line => line.Text?.Trim() ?? string.Empty)
                .Where(text => !string.IsNullOrWhiteSpace(text)));

        return new TranscriptionResult(
            Text: mergedTimedText,
            Model: model,
            CreatedAt: DateTimeOffset.UtcNow,
            Duration: totalDuration > TimeSpan.Zero ? totalDuration : null,
            TokenLogprobs: combinedLogprobs,
            LowConfidenceTokens: combinedLowConfidence,
            TimedLines: mergedTimedLines);
    }

    private async Task<TranscriptionResult> SendRequestAsync(
        Stream audioStream,
        string fileName,
        string mediaType,
        long fileSizeBytes,
        string model,
        TranscriptionRequestMode requestMode,
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
        multipart.Add(
            new StringContent(
                requestMode == TranscriptionRequestMode.TimestampedChunk
                    ? TimestampedResponseFormat
                    : StandardResponseFormat),
            "response_format");
        multipart.Add(new StringContent(Temperature), "temperature");
        multipart.Add(new StringContent(StreamDisabled), "stream");
        multipart.Add(new StringContent(_options.Prompt.Trim()), "prompt");

        if (requestMode == TranscriptionRequestMode.Standard) {
            multipart.Add(new StringContent(ChunkingStrategy), "chunking_strategy");
            multipart.Add(new StringContent(IncludeLogprobs), "include[]");
        }
        else {
            multipart.Add(new StringContent(TimestampGranularitySegment), "timestamp_granularities[]");
        }

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

    private List<PreparedWaveChunk> CreateWaveChunks(string wavePath, TimeSpan? overlap) {
        using var reader = new WaveFileReader(wavePath);

        WaveFormat format = reader.WaveFormat;
        IReadOnlyList<AudioChunkPlan> plans = _audioChunkPlanner.PlanWaveChunks(
            waveDataBytes: reader.Length,
            averageBytesPerSecond: Math.Max(format.AverageBytesPerSecond, 1),
            blockAlign: Math.Max(format.BlockAlign, 1),
            overlap: overlap);

        var chunks = new List<PreparedWaveChunk>(plans.Count);
        byte[] buffer = new byte[81920];

        foreach (AudioChunkPlan plan in plans) {
            string chunkPath = Path.Combine(
                Path.GetTempPath(),
                $"audiotranscript-chunk-{Guid.NewGuid():N}-{plan.ChunkIndex:D4}.wav");

            reader.Position = plan.StartDataOffsetBytes;
            long remaining = plan.DataBytes;

            using (var writer = new WaveFileWriter(chunkPath, format)) {
                while (remaining > 0) {
                    int read = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0) {
                        break;
                    }

                    writer.Write(buffer, 0, read);
                    remaining -= read;
                }
            }

            long chunkFileSize = TryGetFileSize(chunkPath);
            chunks.Add(new PreparedWaveChunk(plan, chunkPath, chunkFileSize));
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
            using FileStream _ = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
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

    private static bool SupportsTimestampedChunkResponses(string model) {
        return string.Equals(model, Whisper1Model, StringComparison.OrdinalIgnoreCase);
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

    private enum TranscriptionRequestMode {
        Standard,
        TimestampedChunk,
    }

    private sealed record PreparedWaveChunk(
        AudioChunkPlan Plan,
        string Path,
        long SizeBytes
    );
}
