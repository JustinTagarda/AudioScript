using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AudioTranscript.Abstractions;
using AudioTranscript.Audio;
using NAudio.Wave;

namespace AudioTranscript.Engines;

public sealed class OpenAiGpt4oMiniTranscriptionEngine : ITranscriptionEngine {
    private readonly AudioStandardizer _audioStandardizer;
    private readonly OpenAiOptions _options;
    private readonly HttpClient _httpClient;

    public OpenAiGpt4oMiniTranscriptionEngine(
        AudioStandardizer audioStandardizer,
        OpenAiOptions options,
        HttpClient httpClient) {
        _audioStandardizer = audioStandardizer;
        _options = options;
        _httpClient = httpClient;
    }

    public string Id => "openai_gpt4o_mini";

    public string DisplayName => "Online: OpenAI gpt-4o-mini-transcribe";

    public EngineCapability Capabilities =>
        EngineCapability.Punctuation
        | EngineCapability.LanguageAutoDetect;

    public async Task<TranscriptUpdate> TranscribeFileAsync(
        string audioFilePath,
        TranscriptionRequest request,
        CancellationToken cancellationToken) {
        EnsureConfigured();

        string standardizedPath = _audioStandardizer.ConvertFileToEngineWav(audioFilePath);

        try {
            string text = await RequestTranscriptFromFileAsync(standardizedPath, request, cancellationToken);

            return new TranscriptUpdate(
                Text: text,
                IsFinal: true,
                CreatedAt: DateTimeOffset.UtcNow,
                Language: ResolveLanguage(request));
        }
        finally {
            TryDeleteFile(standardizedPath);
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
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(ResolveModel()), "model");
        multipart.Add(new StringContent("json"), "response_format");

        string language = ResolveLanguage(request);

        if (!string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)) {
            multipart.Add(new StringContent(language), "language");
        }

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

        using var response = await _httpClient.SendAsync(message, linkedCts.Token);
        string body = await response.Content.ReadAsStringAsync(linkedCts.Token);

        if (!response.IsSuccessStatusCode) {
            throw new InvalidOperationException(
                $"OpenAI request failed ({(int)response.StatusCode} {response.ReasonPhrase}). Body: {body}");
        }

        string text = ExtractTranscript(body);
        return text.Trim();
    }

    private static string ResolveLanguage(TranscriptionRequest request) {
        if (!string.IsNullOrWhiteSpace(request.LanguageHint)) {
            return request.LanguageHint!.Trim();
        }

        return "auto";
    }

    private void EnsureConfigured() {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)) {
            throw new InvalidOperationException(
                "OpenAI API key is missing. Set OPENAI_API_KEY or update settings in-app.");
        }
    }

    private string ResolveModel() {
        string model = _options.Model.Trim();

        if (string.IsNullOrWhiteSpace(model)) {
            return "gpt-4o-mini-transcribe";
        }

        // Accept legacy UI input while routing to the valid transcription model.
        if (string.Equals(model, "gpt-4o-mini", StringComparison.OrdinalIgnoreCase)) {
            return "gpt-4o-mini-transcribe";
        }

        return model;
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
}
