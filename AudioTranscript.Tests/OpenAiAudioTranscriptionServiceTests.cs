using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AudioTranscript.Abstractions;
using AudioTranscript.Audio;
using AudioTranscript.Services;
using Xunit;

namespace AudioTranscript.Tests;

public sealed class OpenAiAudioTranscriptionServiceTests {
    [Fact]
    public async Task TranscribeFileAsync_UsesRequiredRequestFields_AndParsesLogprobs() {
        string filePath = CreateTempFile(".m4a");

        try {
            var handler = new CapturingHttpMessageHandler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(
                        "{\"text\":\"kumusta class\",\"duration\":12.5,\"logprobs\":[{\"token\":\"kumusta\",\"logprob\":-0.2},{\"token\":\"?\",\"logprob\":-2.2}]}",
                        Encoding.UTF8,
                        "application/json"),
                }));

            OpenAiAudioTranscriptionService service = CreateService(handler);

            var result = await service.TranscribeFileAsync(
                filePath,
                OpenAiTranscriptionModelCatalog.Gpt4oTranscribe,
                CancellationToken.None);

            Assert.Equal("kumusta class", result.Text);
            Assert.Equal(2, result.TokenLogprobs.Count);
            Assert.Single(result.LowConfidenceTokens);
            Assert.Equal(HttpMethod.Post, handler.LastRequestMethod);
            Assert.Equal("https://api.openai.com/v1/audio/transcriptions", handler.LastRequestUri?.ToString());
            Assert.Equal("Bearer", handler.LastAuthorization?.Scheme);
            Assert.Equal("sk-test", handler.LastAuthorization?.Parameter);
            AssertFormField(handler.LastRequestBody, "model");
            Assert.Contains(OpenAiTranscriptionModelCatalog.Gpt4oTranscribe, handler.LastRequestBody, StringComparison.Ordinal);
            AssertFormField(handler.LastRequestBody, "response_format");
            Assert.Contains("json", handler.LastRequestBody, StringComparison.Ordinal);
            AssertFormField(handler.LastRequestBody, "temperature");
            AssertFormField(handler.LastRequestBody, "chunking_strategy");
            AssertFormField(handler.LastRequestBody, "include[]");
            AssertFormField(handler.LastRequestBody, "stream");
            AssertFormField(handler.LastRequestBody, "prompt");
            Assert.Contains(OpenAiTranscriptionOptions.DefaultPrompt, handler.LastRequestBody, StringComparison.Ordinal);
            Assert.DoesNotContain("name=\"language\"", handler.LastRequestBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("name=language", handler.LastRequestBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/audio/translations", handler.LastRequestUri?.AbsolutePath ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task TranscribeFileAsync_ThrowsForInvalidFilePath() {
        var handler = new CapturingHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{\"text\":\"ok\"}", Encoding.UTF8, "application/json"),
            }));

        OpenAiAudioTranscriptionService service = CreateService(handler);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.TranscribeFileAsync(
                "Z:\\path\\does-not-exist\\missing-file.wav",
                OpenAiTranscriptionModelCatalog.Gpt4oTranscribe,
                CancellationToken.None));
    }

    [Fact]
    public async Task TranscribeFileAsync_ThrowsForApiFailure() {
        string filePath = CreateTempFile(".wav");

        try {
            var handler = new CapturingHttpMessageHandler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.BadRequest) {
                    Content = new StringContent(
                        "{\"error\":{\"message\":\"bad request payload\"}}",
                        Encoding.UTF8,
                        "application/json"),
                }));

            OpenAiAudioTranscriptionService service = CreateService(handler);

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.TranscribeFileAsync(
                    filePath,
                    OpenAiTranscriptionModelCatalog.Gpt4oMiniTranscribe,
                    CancellationToken.None));

            Assert.Contains("400", ex.Message, StringComparison.Ordinal);
            Assert.Contains("bad request payload", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task TranscribeFileAsync_ThrowsForMalformedResponseJson() {
        string filePath = CreateTempFile(".wav");

        try {
            var handler = new CapturingHttpMessageHandler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent("{ not-json", Encoding.UTF8, "application/json"),
                }));

            OpenAiAudioTranscriptionService service = CreateService(handler);

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.TranscribeFileAsync(
                    filePath,
                    OpenAiTranscriptionModelCatalog.Gpt4oTranscribe,
                    CancellationToken.None));

            Assert.Contains("malformed JSON", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task TranscribeFileAsync_ThrowsWhenResponseTextIsMissing() {
        string filePath = CreateTempFile(".wav");

        try {
            var handler = new CapturingHttpMessageHandler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                }));

            OpenAiAudioTranscriptionService service = CreateService(handler);

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.TranscribeFileAsync(
                    filePath,
                    OpenAiTranscriptionModelCatalog.Gpt4oTranscribe,
                    CancellationToken.None));

            Assert.Contains("did not include transcript text", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task TranscribeFileAsync_SplitsBeforeProcessing_WhenFileIsAtLeast25Mb() {
        string filePath = CreateSilentWaveFile(26L * 1024L * 1024L);

        try {
            int chunkCall = 0;
            var handler = new CapturingHttpMessageHandler(
                responseFactory: (_, _) => {
                    chunkCall++;
                    return Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.OK) {
                            Content = new StringContent(
                                $"{{\"text\":\"chunk-{chunkCall}\"}}",
                                Encoding.UTF8,
                                "application/json"),
                        });
                });

            OpenAiAudioTranscriptionService service = CreateService(handler);

            TranscriptionResult result = await service.TranscribeFileAsync(
                filePath,
                OpenAiTranscriptionModelCatalog.Gpt4oTranscribe,
                CancellationToken.None);

            Assert.True(handler.RequestCount >= 2, "Expected chunked uploads for file >= 25 MB.");
            Assert.Contains("chunk-1", result.Text, StringComparison.Ordinal);
            Assert.Contains("chunk-2", result.Text, StringComparison.Ordinal);
            Assert.NotNull(result.TimedLines);
            Assert.Empty(result.TimedLines!);
            AssertFormField(handler.LastRequestBody, "response_format");
            Assert.Contains("json", handler.LastRequestBody, StringComparison.Ordinal);
            Assert.DoesNotContain("verbose_json", handler.LastRequestBody, StringComparison.Ordinal);
            Assert.DoesNotContain("timestamp_granularities[]", handler.LastRequestBody, StringComparison.OrdinalIgnoreCase);
        }
        finally {
            File.Delete(filePath);
        }
    }

    private static OpenAiAudioTranscriptionService CreateService(CapturingHttpMessageHandler handler) {
        var httpClient = new HttpClient(handler);

        var options = new OpenAiTranscriptionOptions {
            ApiKey = "sk-test",
            Endpoint = new Uri("https://api.openai.com/v1/audio/transcriptions"),
            TimeoutSeconds = 30,
            Prompt = OpenAiTranscriptionOptions.DefaultPrompt,
            LowConfidenceLogprobThreshold = -1.0,
        };

        return new OpenAiAudioTranscriptionService(
            new AudioStandardizer(),
            new AudioChunkPlanner(),
            new TranscriptionSegmentMerger(),
            httpClient,
            options,
            new ProcessLogService(),
            new OpenAiTranscriptionResponseParser());
    }

    private static string CreateTempFile(string extension) {
        string path = Path.Combine(Path.GetTempPath(), $"audiotranscript-test-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        return path;
    }

    private static string CreateSilentWaveFile(long dataBytes) {
        string path = Path.Combine(Path.GetTempPath(), $"audiotranscript-large-{Guid.NewGuid():N}.wav");
        int sampleRate = 16000;
        short channels = 1;
        short bitsPerSample = 16;
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        int byteRate = sampleRate * blockAlign;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write((int)(36 + dataBytes));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write((int)dataBytes);

        stream.SetLength(44 + dataBytes);
        return path;
    }

    private static void AssertFormField(string body, string fieldName) {
        bool quoted = body.Contains($"name=\"{fieldName}\"", StringComparison.OrdinalIgnoreCase);
        bool unquoted = body.Contains($"name={fieldName}", StringComparison.OrdinalIgnoreCase);
        Assert.True(quoted || unquoted, $"Form field '{fieldName}' was not found in multipart body.");
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responseFactory;
        private readonly bool _captureBody;

        public CapturingHttpMessageHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory,
            bool captureBody = true) {
            _responseFactory = responseFactory;
            _captureBody = captureBody;
        }

        public HttpMethod? LastRequestMethod { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        public AuthenticationHeaderValue? LastAuthorization { get; private set; }

        public string LastRequestBody { get; private set; } = string.Empty;

        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            RequestCount++;
            LastRequestMethod = request.Method;
            LastRequestUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization;
            LastRequestBody = !_captureBody || request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return await _responseFactory(request, cancellationToken);
        }
    }
}
