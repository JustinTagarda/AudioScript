using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

namespace VoxTranscribe.Services;

public sealed class OpenAiApiKeyValidationService {
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _httpClient;

    public OpenAiApiKeyValidationService(HttpClient httpClient) {
        _httpClient = httpClient;
    }

    public async Task<OpenAiApiKeyValidationResult> ValidateAsync(string apiKey, CancellationToken cancellationToken) {
        string trimmedApiKey = apiKey?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(trimmedApiKey)) {
            return OpenAiApiKeyValidationResult.Invalid("OpenAI API key is required.");
        }

        if (!trimmedApiKey.StartsWith("sk-", StringComparison.OrdinalIgnoreCase)) {
            return OpenAiApiKeyValidationResult.Invalid("OpenAI API keys should start with 'sk-'.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models?limit=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", trimmedApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try {
            using var timeoutCts = new CancellationTokenSource(ValidationTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            response = await _httpClient.SendAsync(request, linkedCts.Token);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) {
            return OpenAiApiKeyValidationResult.Invalid(
                "OpenAI validation timed out. Check your internet connection and try again.");
        }
        catch (TaskCanceledException) {
            return OpenAiApiKeyValidationResult.Invalid(
                "OpenAI validation timed out. Check your internet connection and try again.");
        }
        catch (HttpRequestException ex) {
            return OpenAiApiKeyValidationResult.Invalid(BuildOpenAiConnectivityMessage(ex));
        }
        catch (Exception ex) {
            return OpenAiApiKeyValidationResult.Invalid($"Unable to validate OpenAI API key: {ex.Message}");
        }

        if (response.IsSuccessStatusCode) {
            return OpenAiApiKeyValidationResult.Valid();
        }

        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        string details = ExtractErrorMessage(responseBody);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) {
            return OpenAiApiKeyValidationResult.Invalid(
                $"OpenAI rejected the API key ({(int)response.StatusCode} {response.ReasonPhrase}). {details}".Trim());
        }

        return OpenAiApiKeyValidationResult.Invalid(
            $"OpenAI validation failed ({(int)response.StatusCode} {response.ReasonPhrase}). {details}".Trim());
    }

    private static string ExtractErrorMessage(string responseBody) {
        if (string.IsNullOrWhiteSpace(responseBody)) {
            return string.Empty;
        }

        try {
            using JsonDocument document = JsonDocument.Parse(responseBody);

            if (document.RootElement.TryGetProperty("error", out JsonElement errorElement)
                && errorElement.TryGetProperty("message", out JsonElement messageElement)) {
                string message = messageElement.GetString() ?? string.Empty;
                return string.IsNullOrWhiteSpace(message) ? string.Empty : message.Trim();
            }
        }
        catch {
            // Ignore parse failures and return empty details.
        }

        return string.Empty;
    }

    private static string BuildOpenAiConnectivityMessage(HttpRequestException exception) {
        if (IsDnsResolutionFailure(exception)) {
            return "Unable to reach OpenAI because DNS could not resolve api.openai.com. " +
                   "Check your internet connection, DNS, VPN/proxy, or firewall and try again.";
        }

        return $"Unable to reach OpenAI service: {exception.Message}";
    }

    private static bool IsDnsResolutionFailure(HttpRequestException exception) {
        if (exception.InnerException is SocketException socketException
            && (socketException.SocketErrorCode == SocketError.HostNotFound
                || socketException.SocketErrorCode == SocketError.NoData
                || socketException.SocketErrorCode == SocketError.TryAgain)) {
            return true;
        }

        return exception.Message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record OpenAiApiKeyValidationResult(bool IsValid, string Message) {
    public static OpenAiApiKeyValidationResult Valid() =>
        new(true, string.Empty);

    public static OpenAiApiKeyValidationResult Invalid(string message) =>
        new(false, message);
}


