using System.Net.Http;

namespace VoxTranscriber.Services;

public sealed class ApplicationVersionCheckService {
    private static readonly Uri[] DefaultOnlineDateCheckEndpoints = {
        new("https://www.microsoft.com"),
        new("https://www.cloudflare.com"),
    };

    public static DateOnly LastSupportedReferenceDate { get; } = new(2026, 6, 18);

    public const string ReferenceTimeZoneDisplayName = "Philippine Time";

    public const string UpdateRequiredMessage =
        "This version of VoxTranscriber needs an update. Please contact Justin Tagarda at justintagarda@gmail.com to get the latest version.";

    private readonly HttpClient _httpClient;
    private readonly ProcessLogService _processLogService;
    private readonly Func<DateTimeOffset> _localUtcNowProvider;
    private readonly IReadOnlyList<Uri> _onlineDateCheckEndpoints;
    private readonly TimeZoneInfo _referenceTimeZone;

    public ApplicationVersionCheckService(
        HttpClient httpClient,
        ProcessLogService processLogService,
        Func<DateTimeOffset>? localUtcNowProvider = null,
        IEnumerable<Uri>? onlineDateCheckEndpoints = null,
        TimeZoneInfo? referenceTimeZone = null) {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _processLogService = processLogService ?? throw new ArgumentNullException(nameof(processLogService));
        _localUtcNowProvider = localUtcNowProvider ?? (() => DateTimeOffset.UtcNow);
        _onlineDateCheckEndpoints = (onlineDateCheckEndpoints ?? DefaultOnlineDateCheckEndpoints).ToArray();
        _referenceTimeZone = referenceTimeZone ?? ResolvePhilippineTimeZone();
    }

    public async Task<ApplicationVersionCheckResult> CheckAsync(CancellationToken cancellationToken) {
        Log("Starting background application version check.");

        (DateTimeOffset? onlineUtcNow, string onlineSource) = await TryResolveOnlineUtcNowAsync(cancellationToken);
        bool usedOnlineTime = onlineUtcNow.HasValue;
        string timeSource = usedOnlineTime ? onlineSource : "local machine clock";
        DateTimeOffset resolvedUtcNow = (onlineUtcNow ?? _localUtcNowProvider()).ToUniversalTime();
        DateTimeOffset resolvedReferenceTime = TimeZoneInfo.ConvertTime(resolvedUtcNow, _referenceTimeZone);
        DateOnly resolvedReferenceDate = ResolveReferenceDate(resolvedUtcNow, _referenceTimeZone);
        bool isExpired = resolvedReferenceDate > LastSupportedReferenceDate;

        Log(
            $"Resolved application check time from {timeSource}: " +
            $"{resolvedReferenceTime:yyyy-MM-dd HH:mm:ss zzz} ({ReferenceTimeZoneDisplayName}).");

        if (isExpired) {
            Log(
                $"Current version requires an update because the reference date " +
                $"{resolvedReferenceDate:yyyy-MM-dd} is beyond {LastSupportedReferenceDate:yyyy-MM-dd}.");
        }
        else {
            Log($"Current version remains valid for reference date {resolvedReferenceDate:yyyy-MM-dd}.");
        }

        return new ApplicationVersionCheckResult(
            IsExpired: isExpired,
            UsedOnlineTime: usedOnlineTime,
            ResolvedUtcNow: resolvedUtcNow,
            ResolvedReferenceTime: resolvedReferenceTime,
            ResolvedReferenceDate: resolvedReferenceDate,
            TimeSource: timeSource,
            UpdateMessage: UpdateRequiredMessage);
    }

    public static DateOnly ResolveReferenceDate(DateTimeOffset utcNow, TimeZoneInfo referenceTimeZone) {
        ArgumentNullException.ThrowIfNull(referenceTimeZone);

        DateTimeOffset referenceTime = TimeZoneInfo.ConvertTime(utcNow.ToUniversalTime(), referenceTimeZone);
        return DateOnly.FromDateTime(referenceTime.Date);
    }

    public static bool IsExpired(DateTimeOffset utcNow, TimeZoneInfo referenceTimeZone) {
        return ResolveReferenceDate(utcNow, referenceTimeZone) > LastSupportedReferenceDate;
    }

    public static TimeZoneInfo ResolvePhilippineTimeZone() {
        string[] candidateIds = {
            "Singapore Standard Time",
            "Asia/Manila",
        };

        foreach (string timeZoneId in candidateIds) {
            try {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException) {
                // Try the next known identifier.
            }
            catch (InvalidTimeZoneException) {
                // Try the next known identifier.
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            id: "Philippine Standard Time",
            baseUtcOffset: TimeSpan.FromHours(8),
            displayName: ReferenceTimeZoneDisplayName,
            standardDisplayName: ReferenceTimeZoneDisplayName);
    }

    private async Task<(DateTimeOffset? UtcNow, string Source)> TryResolveOnlineUtcNowAsync(CancellationToken cancellationToken) {
        foreach (Uri endpoint in _onlineDateCheckEndpoints) {
            DateTimeOffset? dateHeader = await TryReadDateHeaderAsync(endpoint, HttpMethod.Head, cancellationToken);
            if (dateHeader.HasValue) {
                return (dateHeader.Value, endpoint.Host);
            }

            dateHeader = await TryReadDateHeaderAsync(endpoint, HttpMethod.Get, cancellationToken);
            if (dateHeader.HasValue) {
                return (dateHeader.Value, endpoint.Host);
            }
        }

        Log("Falling back to the local machine clock because online time could not be resolved.");
        return (null, string.Empty);
    }

    private async Task<DateTimeOffset?> TryReadDateHeaderAsync(
        Uri endpoint,
        HttpMethod method,
        CancellationToken cancellationToken) {
        using var request = new HttpRequestMessage(method, endpoint);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try {
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linkedCts.Token);

            if (response.Headers.Date is DateTimeOffset dateHeader) {
                Log($"Resolved online date header from {endpoint.Host} via {method}.");
                return dateHeader.ToUniversalTime();
            }

            Log($"Date header was unavailable from {endpoint.Host} via {method}.");
            return null;
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (TaskCanceledException) {
            Log($"Online date check timed out while contacting {endpoint.Host} via {method}.");
            return null;
        }
        catch (HttpRequestException ex) {
            Log($"Online date check failed for {endpoint.Host} via {method}: {ex.Message}");
            return null;
        }
        catch (Exception ex) {
            Log($"Unexpected online date check failure for {endpoint.Host} via {method}: {ex.Message}");
            return null;
        }
    }

    private void Log(string message) {
        _processLogService.Log("VersionCheck", message);
    }
}

public sealed record ApplicationVersionCheckResult(
    bool IsExpired,
    bool UsedOnlineTime,
    DateTimeOffset ResolvedUtcNow,
    DateTimeOffset ResolvedReferenceTime,
    DateOnly ResolvedReferenceDate,
    string TimeSource,
    string UpdateMessage
);


