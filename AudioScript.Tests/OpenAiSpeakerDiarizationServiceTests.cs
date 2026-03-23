using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AudioScript.Abstractions;
using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class OpenAiSpeakerDiarizationServiceTests {
    [Fact]
    public async Task DiarizeAudioFileAsync_UsesDiarizedRequestShape_AndParsesSegments() {
        var handler = new CapturingHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    """
                    {
                      "text": "Speaker 1: hello\nSpeaker 2: hi",
                      "duration": 3.5,
                      "segments": [
                        { "speaker": "A", "start": 0.5, "end": 1.5, "text": "hello" },
                        { "speaker": "B", "start": 2.0, "end": 3.0, "text": "hi" }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            }));

        string audioPath = CreateSilentWaveFile(16000);

        try {
            using var httpClient = new HttpClient(handler);
            var service = new OpenAiSpeakerDiarizationService(
                httpClient,
                new OpenAiTranscriptionOptions {
                    ApiKey = "sk-test",
                    Endpoint = new Uri("https://api.openai.com/v1/audio/transcriptions"),
                    TimeoutSeconds = 30,
                    DiarizationChunkingStrategy = "auto",
                },
                new ProcessLogService(),
                new OpenAiSpeakerDiarizationResponseParser());

            SpeakerDiarizationResult result = await service.DiarizeAudioFileAsync(audioPath, CancellationToken.None);

            Assert.Equal(2, result.Segments.Count);
            Assert.Equal("A", result.Segments[0].Speaker);
            Assert.Equal(TimeSpan.FromSeconds(0.5), result.Segments[0].StartOffset);
            Assert.Equal(TimeSpan.FromSeconds(1.5), result.Segments[0].EndOffset);
            Assert.Equal("hello", result.Segments[0].Text);
            Assert.Equal(HttpMethod.Post, handler.LastRequestMethod);
            Assert.Equal("https://api.openai.com/v1/audio/transcriptions", handler.LastRequestUri?.ToString());
            Assert.Equal("Bearer", handler.LastAuthorization?.Scheme);
            Assert.Equal("sk-test", handler.LastAuthorization?.Parameter);
            AssertFormField(handler.LastRequestBody, "model");
            AssertFormField(handler.LastRequestBody, "response_format");
            AssertFormField(handler.LastRequestBody, "chunking_strategy");
            Assert.Contains("gpt-4o-transcribe-diarize", handler.LastRequestBody, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("diarized_json", handler.LastRequestBody, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("audio/wav", handler.LastRequestBody, StringComparison.OrdinalIgnoreCase);
            AssertNoFormField(handler.LastRequestBody, "prompt");
            AssertNoFormField(handler.LastRequestBody, "language");
        }
        finally {
            File.Delete(audioPath);
        }
    }

    [Fact]
    public async Task DiarizeAudioFileAsync_IncludesKnownSpeakerReferences_WhenProvided() {
        var handler = new CapturingHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    """
                    {
                      "text": "speaker_1: hello",
                      "segments": [
                        { "speaker": "speaker_1", "start": 0.0, "end": 1.0, "text": "hello" }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            }));

        string audioPath = CreateSilentWaveFile(32000);
        string referencePath = CreateSilentWaveFile(16000);

        try {
            using var httpClient = new HttpClient(handler);
            var service = new OpenAiSpeakerDiarizationService(
                httpClient,
                new OpenAiTranscriptionOptions {
                    ApiKey = "sk-test",
                    Endpoint = new Uri("https://api.openai.com/v1/audio/transcriptions"),
                    TimeoutSeconds = 30,
                    DiarizationChunkingStrategy = "auto",
                },
                new ProcessLogService(),
                new OpenAiSpeakerDiarizationResponseParser());

            _ = await service.DiarizeAudioFileAsync(
                audioPath,
                new[] {
                    new KnownSpeakerReference("speaker_1", referencePath),
                },
                CancellationToken.None);

            AssertFormField(handler.LastRequestBody, "known_speaker_names[]");
            AssertFormField(handler.LastRequestBody, "known_speaker_references[]");
            Assert.Contains("speaker_1", handler.LastRequestBody, StringComparison.Ordinal);
            Assert.Contains("data:audio/wav;base64,", handler.LastRequestBody, StringComparison.OrdinalIgnoreCase);
        }
        finally {
            File.Delete(audioPath);
            File.Delete(referencePath);
        }
    }

    [Fact]
    public void Parse_MapsDiarizedSegments_WhenTextFieldIsMissing() {
        const string responseBody =
            """
            {
              "segments": [
                { "speaker": "A", "start": 1.0, "end": 2.0, "text": "hello there" },
                { "speaker": "B", "start": 5.0, "end": 6.5, "text": "general kenobi" }
              ]
            }
            """;

        var parser = new OpenAiSpeakerDiarizationResponseParser();

        SpeakerDiarizationResult result = parser.Parse(
            responseBody,
            OpenAiTranscriptionModelCatalog.Gpt4oTranscribeDiarize);

        Assert.Equal("A: hello there" + Environment.NewLine + "B: general kenobi", result.Text);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), result.Segments[1].StartOffset);
        Assert.Equal(TimeSpan.FromSeconds(6.5), result.Segments[1].EndOffset);
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

    private static string CreateSilentWaveFile(long dataBytes) {
        string path = Path.Combine(Path.GetTempPath(), $"AudioScript-diarize-audio-{Guid.NewGuid():N}.wav");
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

