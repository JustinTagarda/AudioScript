using System.Diagnostics;
using Windows.ApplicationModel;

namespace AudioScript.Services.Store;

public sealed class StoreNavigationService : IStoreNavigationService
{
    private readonly ProcessLogService _processLogService;
    private readonly string? _configuredStoreUri;

    public StoreNavigationService(ProcessLogService processLogService, string? configuredStoreUri = null)
    {
        _processLogService = processLogService ?? throw new ArgumentNullException(nameof(processLogService));
        _configuredStoreUri = NormalizeStoreUri(configuredStoreUri);
    }

    public bool CanOpenAppStorePage => !string.IsNullOrWhiteSpace(ResolveStoreUri());

    public Task OpenAppStorePageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? uri = ResolveStoreUri();
        if (string.IsNullOrWhiteSpace(uri))
        {
            throw new InvalidOperationException("Microsoft Store page is not configured for this build.");
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _processLogService.LogException("Store", "open_store_page_failed", ex);
            throw;
        }

        return Task.CompletedTask;
    }

    private string? ResolveStoreUri()
    {
        if (!string.IsNullOrWhiteSpace(_configuredStoreUri))
        {
            return _configuredStoreUri;
        }

        try
        {
            string familyName = Package.Current.Id.FamilyName;
            if (!string.IsNullOrWhiteSpace(familyName))
            {
                return $"ms-windows-store://pdp/?PFN={Uri.EscapeDataString(familyName)}";
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? NormalizeStoreUri(string? configuredStoreUri)
    {
        string trimmed = configuredStoreUri?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
            && (string.Equals(uri.Scheme, "ms-windows-store", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            ? trimmed
            : null;
    }
}
