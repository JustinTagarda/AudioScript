using System.Net;
using VoxTranscribe.Services;
using Xunit;

namespace VoxTranscribe.Tests;

public sealed class ApplicationVersionCheckServiceTests {
    [Fact]
    public async Task CheckAsync_UsesOnlineDateHeader_WhenAvailable() {
        DateTimeOffset onlineUtcNow = new(2026, 6, 18, 12, 15, 0, TimeSpan.Zero);
        using var httpClient = new HttpClient(new StubDateHeaderMessageHandler(onlineUtcNow));
        var service = new ApplicationVersionCheckService(
            httpClient,
            new ProcessLogService(),
            localUtcNowProvider: () => new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
            onlineDateCheckEndpoints: new[] { new Uri("https://example.test") });

        ApplicationVersionCheckResult result = await service.CheckAsync(CancellationToken.None);

        Assert.True(result.UsedOnlineTime);
        Assert.Equal(onlineUtcNow, result.ResolvedUtcNow);
        Assert.False(result.IsExpired);
        Assert.Equal(new DateOnly(2026, 6, 18), result.ResolvedReferenceDate);
    }

    [Fact]
    public async Task CheckAsync_FallsBackToLocalTime_WhenOnlineCheckFails() {
        DateTimeOffset localUtcNow = new(2026, 6, 19, 1, 0, 0, TimeSpan.Zero);
        using var httpClient = new HttpClient(new ThrowingMessageHandler());
        var service = new ApplicationVersionCheckService(
            httpClient,
            new ProcessLogService(),
            localUtcNowProvider: () => localUtcNow,
            onlineDateCheckEndpoints: new[] { new Uri("https://example.test") });

        ApplicationVersionCheckResult result = await service.CheckAsync(CancellationToken.None);

        Assert.False(result.UsedOnlineTime);
        Assert.Equal(localUtcNow, result.ResolvedUtcNow);
        Assert.True(result.IsExpired);
        Assert.Equal(new DateOnly(2026, 6, 19), result.ResolvedReferenceDate);
    }

    [Fact]
    public void IsExpired_UsesPhilippineTimeBoundary() {
        TimeZoneInfo philippineTimeZone = ApplicationVersionCheckService.ResolvePhilippineTimeZone();

        bool validOnLastDay = ApplicationVersionCheckService.IsExpired(
            new DateTimeOffset(2026, 6, 18, 15, 59, 59, TimeSpan.Zero),
            philippineTimeZone);
        bool expiredAfterBoundary = ApplicationVersionCheckService.IsExpired(
            new DateTimeOffset(2026, 6, 18, 16, 0, 0, TimeSpan.Zero),
            philippineTimeZone);

        Assert.False(validOnLastDay);
        Assert.True(expiredAfterBoundary);
    }

    private sealed class StubDateHeaderMessageHandler : HttpMessageHandler {
        private readonly DateTimeOffset _dateHeaderValue;

        public StubDateHeaderMessageHandler(DateTimeOffset dateHeaderValue) {
            _dateHeaderValue = dateHeaderValue;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Date = _dateHeaderValue;
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingMessageHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            throw new HttpRequestException("Simulated network failure.");
        }
    }
}


