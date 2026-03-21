using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using VoxTranscribe.Abstractions;

namespace VoxTranscribe.Services;

public sealed class OpenAiSpeakerDiarizationService {
    private const string ResponseFormat = "diarized_json";

    private readonly HttpClient _httpClient;
    private readonly OpenAiTranscriptionOptions _options;
    private readonly ProcessLogService _processLogService;
    private readonly OpenAiSpeakerDiarizationResponseParser _responseParser;

    public OpenAiSpeakerDiarizationService(
        HttpClient httpClient,
        OpenAiTranscriptionOptions options,
        ProcessLogService processLogService,
        OpenAiSpeakerDiarizationResponseParser responseParser) {
        _httpClient = httpClient;
        _options = options;
        _processLogService = processLogService;
        _responseParser = responseParser;
    }

    public async Task<SpeakerDiarizationResult> DiarizeAudioFileAsync(
        string audioFilePath,
        CancellationToken cancellationToken) {
        return await DiarizeAudioFileAsync(
            audioFilePath,
            knownSpeakerReferences: null,
            cancellationToken);
    }

    public async Task<SpeakerDiarizationResult> DiarizeAudioFileAsync(
        string audioFilePath,
        IReadOnlyList<KnownSpeakerReference>? knownSpeakerReferences,
        CancellationToken cancellationToken) {
        string fullPath = ValidateAudioFilePath(audioFilePath);
        EnsureConfigured();

        string fileName = Path.GetFileName(fullPath);
        var fileInfo = new FileInfo(fullPath);
        int knownSpeakerReferenceCount = knownSpeakerReferences?.Count ?? 0;
        int timeoutSeconds = Math.Max(_options.SpeakerDiarizationTimeoutSeconds, 60);

        Log(
            $"Submitting speaker diarization for '{fileName}' " +
            $"({fileInfo.Length:N0} bytes) using model '{OpenAiTranscriptionModelCatalog.Gpt4oTranscribeDiarize}'" +
            $"{(knownSpeakerReferenceCount > 0 ? $" with {knownSpeakerReferenceCount:N0} known speaker reference(s)" : string.Empty)} " +
            $"and timeout {timeoutSeconds:N0}s.");

        var stopwatch = Stopwatch.StartNew();
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try {
            SpeakerDiarizationResult result = await SendRequestAsync(
                fullPath,
                fileName,
                knownSpeakerReferences,
                linkedCts.Token);
            stopwatch.Stop();

            Log(
                $"Speaker diarization for '{fileName}' completed in {stopwatch.Elapsed.TotalSeconds:F2}s " +
                $"with {result.Segments.Count:N0} segment(s).");

            return result;
        }
        catch (TaskCanceledException ex) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested) {
            throw new TimeoutException("OpenAI speaker diarization request timed out.", ex);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (HttpRequestException ex) {
            throw new InvalidOperationException(BuildConnectivityMessage(ex), ex);
        }
    }

    private void EnsureConfigured() {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)) {
            throw new InvalidOperationException("OpenAI API key is required for speaker diarization.");
        }

        if (_options.Endpoint is null) {
            throw new InvalidOperationException("OpenAI transcription endpoint is not configured.");
        }
    }

    private static string ValidateAudioFilePath(string audioFilePath) {
        if (string.IsNullOrWhiteSpace(audioFilePath)) {
            throw new ArgumentException("Audio file path is required.", nameof(audioFilePath));
        }

        string fullPath = Path.GetFullPath(audioFilePath.Trim());

        if (!File.Exists(fullPath)) {
            throw new FileNotFoundException("Audio file was not found.", fullPath);
        }

        using FileStream _ = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return fullPath;
    }

    private async Task<SpeakerDiarizationResult> SendRequestAsync(
        string fullPath,
        string fileName,
        IReadOnlyList<KnownSpeakerReference>? knownSpeakerReferences,
        CancellationToken cancellationToken) {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(OpenAiTranscriptionModelCatalog.Gpt4oTranscribeDiarize), "model");
        multipart.Add(new StringContent(ResponseFormat), "response_format");
        multipart.Add(new StringContent(_options.DiarizationChunkingStrategy.Trim()), "chunking_strategy");

        using var audioStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var audioContent = new StreamContent(audioStream);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(ResolveContentType(fileName));
        multipart.Add(audioContent, "file", fileName);

        if (knownSpeakerReferences is not null) {
            foreach (KnownSpeakerReference reference in knownSpeakerReferences.Where(reference =>
                         !string.IsNullOrWhiteSpace(reference.Name)
                         && !string.IsNullOrWhiteSpace(reference.AudioFilePath))) {
                multipart.Add(new StringContent(reference.Name.Trim()), "known_speaker_names[]");
                multipart.Add(new StringContent(BuildDataUrl(reference.AudioFilePath)), "known_speaker_references[]");
            }
        }

        request.Content = multipart;

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) {
            string apiErrorMessage = ExtractApiErrorMessage(responseBody);

            Log(
                $"Speaker diarization request failed with status {(int)response.StatusCode} " +
                $"({response.ReasonPhrase}) for '{fileName}'.");

            throw CreateFailure(response.StatusCode, response.ReasonPhrase, apiErrorMessage);
        }

        try {
            return _responseParser.Parse(
                responseBody,
                OpenAiTranscriptionModelCatalog.Gpt4oTranscribeDiarize);
        }
        catch (Exception ex) {
            Log($"Speaker diarization response parsing failed for '{fileName}': {ex.Message}");
            throw;
        }
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

        return $"Unable to reach OpenAI speaker diarization service: {exception.Message}";
    }

    private static Exception CreateFailure(
        HttpStatusCode statusCode,
        string? reasonPhrase,
        string apiErrorMessage) {
        string message = string.IsNullOrWhiteSpace(apiErrorMessage)
            ? $"OpenAI speaker diarization request failed ({(int)statusCode} {reasonPhrase})."
            : $"OpenAI speaker diarization request failed ({(int)statusCode} {reasonPhrase}): {apiErrorMessage}";

        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) {
            return new UnauthorizedAccessException(message);
        }

        return new InvalidOperationException(message);
    }

    private void Log(string message) {
        _processLogService.Log("SpeakerDiarization", message);
    }

    private static string BuildDataUrl(string audioFilePath) {
        string fullPath = ValidateAudioFilePath(audioFilePath);
        string fileName = Path.GetFileName(fullPath);
        string contentType = ResolveContentType(fileName);
        string base64 = Convert.ToBase64String(File.ReadAllBytes(fullPath));
        return $"data:{contentType};base64,{base64}";
    }

    private static string ResolveContentType(string fileName) {
        string extension = Path.GetExtension(fileName)?.Trim().ToLowerInvariant() ?? string.Empty;

        return extension switch {
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            ".ogg" => "audio/ogg",
            ".aac" => "audio/aac",
            ".m4a" => "audio/mp4",
            ".mp4" => "audio/mp4",
            ".wma" => "audio/x-ms-wma",
            _ => "application/octet-stream",
        };
    }
}
