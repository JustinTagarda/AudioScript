using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using AudioTranscript.Abstractions;
using AudioTranscript.Audio;
using NAudio.Wave;

namespace AudioTranscript.Services;

public sealed class PlaybackAudioTranscriptionService : IPlaybackAudioTranscriptionService {
    private const string ResponseFormat = "json";
    private const string Temperature = "0";
    private const string StreamDisabled = "false";

    private readonly AudioStandardizer _audioStandardizer;
    private readonly HttpClient _httpClient;
    private readonly OpenAiTranscriptionOptions _options;
    private readonly ProcessLogService _processLogService;
    private readonly OpenAiTranscriptionResponseParser _responseParser;

    public PlaybackAudioTranscriptionService(
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

    public async Task<string> TranscribePcmChunkAsync(
        byte[] pcmAudio,
        WaveFormat sourceFormat,
        string model,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(pcmAudio);
        ArgumentNullException.ThrowIfNull(sourceFormat);

        string validatedModel = ValidateModel(model);
        EnsureConfigured();

        if (pcmAudio.Length == 0) {
            throw new InvalidOperationException("Playback audio chunk was empty.");
        }

        double approximateDurationSeconds = sourceFormat.AverageBytesPerSecond <= 0
            ? 0
            : pcmAudio.Length / (double)sourceFormat.AverageBytesPerSecond;
        byte[] standardizedWaveBytes = _audioStandardizer.ConvertPcmBytesToEngineWav(pcmAudio, sourceFormat);
        string fileName = $"playback-{Guid.NewGuid():N}.wav";

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(validatedModel), "model");
        multipart.Add(new StringContent(ResponseFormat), "response_format");
        multipart.Add(new StringContent(Temperature), "temperature");
        multipart.Add(new StringContent(StreamDisabled), "stream");
        multipart.Add(new StringContent(_options.Prompt.Trim()), "prompt");

        using var audioStream = new MemoryStream(standardizedWaveBytes, writable: false);
        var audioContent = new StreamContent(audioStream);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        multipart.Add(audioContent, "file", fileName);
        request.Content = multipart;

        Log(
            $"Submitting playback transcription chunk '{fileName}' " +
            $"(pcm={pcmAudio.Length:N0} bytes, wav={standardizedWaveBytes.Length:N0} bytes, " +
            $"duration~={approximateDurationSeconds:F2}s) using model '{validatedModel}'.");

        var stopwatch = Stopwatch.StartNew();
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
            throw new TimeoutException("OpenAI playback transcription request timed out.", ex);
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
                    ? $"OpenAI playback transcription request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"OpenAI playback transcription request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {apiErrorMessage}";

                Log(
                    $"Playback transcription request failed with status {(int)response.StatusCode} " +
                    $"({response.ReasonPhrase}) for '{fileName}'.");

                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden) {
                    throw new UnauthorizedAccessException(message);
                }

                throw new InvalidOperationException(message);
            }

            TranscriptionResult result;

            try {
                result = _responseParser.Parse(
                    responseBody,
                    validatedModel,
                    _options.LowConfidenceLogprobThreshold);
            }
            catch (Exception ex) {
                Log($"Playback transcription response parsing failed for '{fileName}': {ex.Message}");
                throw;
            }

            stopwatch.Stop();
            Log(
                $"Playback transcription chunk '{fileName}' completed in {stopwatch.Elapsed.TotalSeconds:F2}s " +
                $"with {result.Text.Length:N0} characters.");

            return result.Text.Trim();
        }
    }

    private void EnsureConfigured() {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)) {
            throw new InvalidOperationException("OpenAI API key is required for playback transcription.");
        }

        if (_options.Endpoint is null) {
            throw new InvalidOperationException("OpenAI transcription endpoint is not configured.");
        }
    }

    private static string ValidateModel(string model) {
        string trimmed = model?.Trim() ?? string.Empty;

        if (!OpenAiTranscriptionModelCatalog.IsSupported(trimmed)) {
            throw new InvalidOperationException(
                $"Unsupported playback transcription model '{model}'. " +
                $"Use '{OpenAiTranscriptionModelCatalog.Gpt4oTranscribe}' or '{OpenAiTranscriptionModelCatalog.Gpt4oMiniTranscribe}'.");
        }

        return trimmed;
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

        return $"Unable to reach OpenAI playback transcription service: {exception.Message}";
    }

    private void Log(string message) {
        _processLogService.Log("PlaybackOpenAI", message);
    }
}
