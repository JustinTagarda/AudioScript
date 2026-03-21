using System.Net;
using System.Net.Http.Headers;
using System.Text;
using VoxTranscribe.Audio;
using VoxTranscribe.Services;
using NAudio.Wave;
using Xunit;

namespace VoxTranscribe.Tests;

public sealed class PlaybackTranscriptionServiceTests {
    [Fact]
    public async Task TranscribePcmChunkAsync_UsesPlaybackRequestShape_AndParsesText() {
        var handler = new CapturingHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    "{\"text\":\"interim text\"}",
                    Encoding.UTF8,
                    "application/json"),
            }));

        using var httpClient = new HttpClient(handler);
        var service = new PlaybackTranscriptionService(
            new AudioStandardizer(),
            httpClient,
            new OpenAiTranscriptionOptions {
                ApiKey = "sk-test",
                Endpoint = new Uri("https://api.openai.com/v1/audio/transcriptions"),
                TimeoutSeconds = 30,
                Prompt = OpenAiTranscriptionOptions.DefaultPrompt,
            },
            new ProcessLogService(),
            new OpenAiTranscriptionResponseParser());

        byte[] pcmAudio = new byte[3200];
        string text = await service.TranscribePcmChunkAsync(
            pcmAudio,
            new WaveFormat(16000, 16, 1),
            OpenAiTranscriptionModelCatalog.Gpt4oMiniTranscribe,
            CancellationToken.None);

        Assert.Equal("interim text", text);
        Assert.Equal(HttpMethod.Post, handler.LastRequestMethod);
        Assert.Equal("https://api.openai.com/v1/audio/transcriptions", handler.LastRequestUri?.ToString());
        Assert.Equal("Bearer", handler.LastAuthorization?.Scheme);
        Assert.Equal("sk-test", handler.LastAuthorization?.Parameter);
        AssertFormField(handler.LastRequestBody, "model");
        AssertFormField(handler.LastRequestBody, "response_format");
        AssertFormField(handler.LastRequestBody, "temperature");
        AssertFormField(handler.LastRequestBody, "stream");
        AssertFormField(handler.LastRequestBody, "prompt");
        AssertFormField(handler.LastRequestBody, "language");
        Assert.Contains("ceb", handler.LastRequestBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("audio/wav", handler.LastRequestBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chunking_strategy", handler.LastRequestBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("include[]", handler.LastRequestBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranscribePcmChunkAsync_RetriesWithoutLanguage_WhenLanguageHintIsRejected() {
        int requestCount = 0;
        List<string> bodies = new();

        var handler = new CapturingHttpMessageHandler(async (request, cancellationToken) => {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            bodies.Add(body);
            requestCount++;

            if (requestCount == 1) {
                return new HttpResponseMessage(HttpStatusCode.BadRequest) {
                    Content = new StringContent(
                        "{\"error\":{\"message\":\"Unsupported language value for this model.\"}}",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    "{\"text\":\"final text\"}",
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        using var httpClient = new HttpClient(handler);
        var service = new PlaybackTranscriptionService(
            new AudioStandardizer(),
            httpClient,
            new OpenAiTranscriptionOptions {
                ApiKey = "sk-test",
                Endpoint = new Uri("https://api.openai.com/v1/audio/transcriptions"),
                TimeoutSeconds = 30,
                Prompt = OpenAiTranscriptionOptions.DefaultPrompt,
                PlaybackLanguageHint = "ceb",
            },
            new ProcessLogService(),
            new OpenAiTranscriptionResponseParser());

        string text = await service.TranscribePcmChunkAsync(
            new byte[3200],
            new WaveFormat(16000, 16, 1),
            OpenAiTranscriptionModelCatalog.Gpt4oMiniTranscribe,
            CancellationToken.None);

        Assert.Equal("final text", text);
        Assert.Equal(2, requestCount);
        AssertFormField(bodies[0], "language");
        AssertNoFormField(bodies[1], "language");
    }

    private static void AssertFormField(string body, string fieldName) {
        bool quoted = body.Contains($"name=\"{fieldName}\"", StringComparison.OrdinalIgnoreCase);
        bool unquoted = body.Contains($"name={fieldName}", StringComparison.OrdinalIgnoreCase);
        Assert.True(quoted || unquoted, $"Form field '{fieldName}' was not found in multipart body.");
    }

    private static void AssertNoFormField(string body, string fieldName) {
        bool quoted = body.Contains($"name=\"{fieldName}\"", StringComparison.OrdinalIgnoreCase);
        bool unquoted = body.Contains($"name={fieldName}", StringComparison.OrdinalIgnoreCase);
        Assert.False(quoted || unquoted, $"Form field '{fieldName}' was unexpectedly found in multipart body.");
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responseFactory;

        public CapturingHttpMessageHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) {
            _responseFactory = responseFactory;
        }

        public HttpMethod? LastRequestMethod { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        public AuthenticationHeaderValue? LastAuthorization { get; private set; }

        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            LastRequestMethod = request.Method;
            LastRequestUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization;
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return await _responseFactory(request, cancellationToken);
        }
    }
}



