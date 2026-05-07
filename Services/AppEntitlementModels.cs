using Windows.Services.Store;

namespace AudioScript.Services;

public sealed record AppEntitlementSnapshot(
    bool IsPackaged,
    bool HasPremium,
    bool IsPremiumProductAvailable,
    string PremiumProductDisplayName,
    string StatusMessage)
{
    public static AppEntitlementSnapshot Development(string premiumProductDisplayName) =>
        new(
            IsPackaged: false,
            HasPremium: true,
            IsPremiumProductAvailable: false,
            PremiumProductDisplayName: premiumProductDisplayName,
            StatusMessage: "Development build: Premium features unlocked.");
}

public enum PremiumPurchaseStatus
{
    Succeeded,
    AlreadyOwned,
    Canceled,
    NotAvailable,
    NetworkError,
    ServerError,
    UnknownError,
}

public sealed record PremiumPurchaseResult(
    PremiumPurchaseStatus Status,
    string Message);

public sealed class StoreEntitlementServiceOptions
{
    public bool TreatUnpackagedBuildsAsPremium { get; init; } = true;

    public string PremiumProductDisplayName { get; init; } = "AudioScript Premium";

    public string PremiumStoreId { get; init; } = string.Empty;

    public string PremiumKeyword { get; init; } = "premium";
}

public interface IEntitlementService : IAsyncDisposable
{
    AppEntitlementSnapshot CurrentSnapshot { get; }

    event EventHandler<AppEntitlementSnapshot>? SnapshotChanged;

    Task RefreshAsync(CancellationToken cancellationToken = default);

    Task<PremiumPurchaseResult> RequestPremiumPurchaseAsync(CancellationToken cancellationToken = default);
}

public enum AppFeature
{
    LiveTranscription,
    SpeakerDiarization,
    PremiumModelInstall,
}

public static class AppFeatureAccess
{
    public static bool IsPremiumOnlyModel(string? modelId)
    {
        string normalized = modelId?.Trim() ?? string.Empty;
        return string.Equals(normalized, TranscriptionModelCatalog.WhisperMedium, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, TranscriptionModelCatalog.WhisperLargeV3, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, TranscriptionModelCatalog.WhisperLargeV3Turbo, StringComparison.OrdinalIgnoreCase);
    }

    public static bool CanUseModel(string? modelId, bool hasPremium)
    {
        return hasPremium || !IsPremiumOnlyModel(modelId);
    }

    public static bool CanInstallModel(string? modelId, bool hasPremium)
    {
        return hasPremium || !IsPremiumOnlyModel(modelId);
    }

    public static bool CanAccessFeature(AppFeature feature, bool hasPremium)
    {
        return feature switch
        {
            AppFeature.LiveTranscription => hasPremium,
            AppFeature.SpeakerDiarization => hasPremium,
            AppFeature.PremiumModelInstall => hasPremium,
            _ => false,
        };
    }
}

public sealed class StoreEntitlementService : IEntitlementService
{
    private readonly IAppVersionProvider _appVersionProvider;
    private readonly ProcessLogService _processLogService;
    private readonly Func<IntPtr>? _ownerWindowHandleProvider;
    private readonly StoreEntitlementServiceOptions _options;
    private readonly object _sync = new();
    private AppEntitlementSnapshot _currentSnapshot;

    public StoreEntitlementService(
        IAppVersionProvider appVersionProvider,
        ProcessLogService processLogService,
        Func<IntPtr>? ownerWindowHandleProvider = null,
        StoreEntitlementServiceOptions? options = null)
    {
        _appVersionProvider = appVersionProvider ?? throw new ArgumentNullException(nameof(appVersionProvider));
        _processLogService = processLogService ?? throw new ArgumentNullException(nameof(processLogService));
        _ownerWindowHandleProvider = ownerWindowHandleProvider;
        _options = options ?? new StoreEntitlementServiceOptions();
        _currentSnapshot = _appVersionProvider.IsPackaged || !_options.TreatUnpackagedBuildsAsPremium
            ? new AppEntitlementSnapshot(
                IsPackaged: _appVersionProvider.IsPackaged,
                HasPremium: false,
                IsPremiumProductAvailable: false,
                PremiumProductDisplayName: _options.PremiumProductDisplayName,
                StatusMessage: string.Empty)
            : AppEntitlementSnapshot.Development(_options.PremiumProductDisplayName);
    }

    public event EventHandler<AppEntitlementSnapshot>? SnapshotChanged;

    public AppEntitlementSnapshot CurrentSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _currentSnapshot;
            }
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!_appVersionProvider.IsPackaged)
        {
            Publish(_options.TreatUnpackagedBuildsAsPremium
                ? AppEntitlementSnapshot.Development(_options.PremiumProductDisplayName)
                : new AppEntitlementSnapshot(
                    IsPackaged: false,
                    HasPremium: false,
                    IsPremiumProductAvailable: false,
                    PremiumProductDisplayName: _options.PremiumProductDisplayName,
                    StatusMessage: "Premium entitlement is unavailable outside the Microsoft Store package."));
            return Task.CompletedTask;
        }

        return RefreshPackagedAsync(cancellationToken);
    }

    public async Task<PremiumPurchaseResult> RequestPremiumPurchaseAsync(CancellationToken cancellationToken = default)
    {
        AppEntitlementSnapshot snapshot = CurrentSnapshot;
        if (snapshot.HasPremium)
        {
            return new PremiumPurchaseResult(
                PremiumPurchaseStatus.AlreadyOwned,
                $"{snapshot.PremiumProductDisplayName} is already unlocked.");
        }

        if (!_appVersionProvider.IsPackaged)
        {
            return new PremiumPurchaseResult(
                PremiumPurchaseStatus.NotAvailable,
                "Premium purchase is only available in the Microsoft Store package.");
        }

        StoreContext context = CreateStoreContext();
        StoreProduct? premiumProduct = await ResolvePremiumProductAsync(context, cancellationToken).ConfigureAwait(true);
        if (premiumProduct is null)
        {
            Publish(CurrentSnapshot with
            {
                HasPremium = false,
                IsPremiumProductAvailable = false,
                StatusMessage = $"{_options.PremiumProductDisplayName} is not currently available in Microsoft Store.",
            });
            return new PremiumPurchaseResult(
                PremiumPurchaseStatus.NotAvailable,
                $"{_options.PremiumProductDisplayName} is not currently available in Microsoft Store.");
        }

        try
        {
            StorePurchaseResult result = await premiumProduct.RequestPurchaseAsync().AsTask(cancellationToken);
            PremiumPurchaseResult purchaseResult = MapPurchaseResult(result, premiumProduct.Title);
            if (purchaseResult.Status is PremiumPurchaseStatus.Succeeded or PremiumPurchaseStatus.AlreadyOwned)
            {
                await RefreshPackagedAsync(cancellationToken).ConfigureAwait(true);
            }

            Log("premium_purchase_completed", $"status={purchaseResult.Status}; storeId={premiumProduct.StoreId}");
            return purchaseResult;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogException("premium_purchase_failed", ex);
            return new PremiumPurchaseResult(
                PremiumPurchaseStatus.UnknownError,
                $"Unable to open Microsoft Store purchase flow: {ex.Message}");
        }
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private async Task RefreshPackagedAsync(CancellationToken cancellationToken)
    {
        try
        {
            StoreContext context = CreateStoreContext();
            StoreAppLicense license = await context.GetAppLicenseAsync().AsTask(cancellationToken);
            StoreProduct? premiumProduct = await ResolvePremiumProductAsync(context, cancellationToken).ConfigureAwait(false);
            bool hasPremium = premiumProduct is not null
                && license.AddOnLicenses.TryGetValue(premiumProduct.StoreId, out StoreLicense? addOnLicense)
                && addOnLicense.IsActive;
            string productName = string.IsNullOrWhiteSpace(premiumProduct?.Title)
                ? _options.PremiumProductDisplayName
                : premiumProduct!.Title;
            string statusMessage = hasPremium
                ? $"{productName} is unlocked."
                : premiumProduct is null
                    ? $"{_options.PremiumProductDisplayName} is not currently available in Microsoft Store."
                    : $"{productName} is available for purchase in Microsoft Store.";

            Publish(new AppEntitlementSnapshot(
                IsPackaged: true,
                HasPremium: hasPremium,
                IsPremiumProductAvailable: premiumProduct is not null,
                PremiumProductDisplayName: productName,
                StatusMessage: statusMessage));
            Log(
                "premium_entitlement_refreshed",
                $"hasPremium={hasPremium}; productAvailable={premiumProduct is not null}; productName='{productName}'.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogException("premium_entitlement_refresh_failed", ex);
            Publish(new AppEntitlementSnapshot(
                IsPackaged: true,
                HasPremium: false,
                IsPremiumProductAvailable: false,
                PremiumProductDisplayName: _options.PremiumProductDisplayName,
                StatusMessage: $"Unable to verify {_options.PremiumProductDisplayName} entitlement right now."));
        }
    }

    private async Task<StoreProduct?> ResolvePremiumProductAsync(StoreContext context, CancellationToken cancellationToken)
    {
        StoreProductQueryResult result = await context.GetAssociatedStoreProductsAsync(new[] { "Durable" }).AsTask(cancellationToken);
        IEnumerable<StoreProduct> products = result.Products?.Values ?? Array.Empty<StoreProduct>();
        string configuredStoreId = _options.PremiumStoreId?.Trim() ?? string.Empty;
        string keyword = _options.PremiumKeyword?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(configuredStoreId))
        {
            StoreProduct? exact = products.FirstOrDefault(product =>
                string.Equals(product.StoreId, configuredStoreId, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            StoreProduct? keywordMatch = products.FirstOrDefault(product =>
                ContainsKeyword(product.Title, keyword)
                || ContainsKeyword(product.StoreId, keyword));
            if (keywordMatch is not null)
            {
                return keywordMatch;
            }
        }

        StoreProduct[] durableProducts = products.ToArray();
        return durableProducts.Length == 1 ? durableProducts[0] : null;
    }

    private StoreContext CreateStoreContext()
    {
        StoreContext context = StoreContext.GetDefault();
        IntPtr ownerWindowHandle = _ownerWindowHandleProvider?.Invoke() ?? IntPtr.Zero;
        if (ownerWindowHandle == IntPtr.Zero)
        {
            return context;
        }

        try
        {
            WinRT.Interop.InitializeWithWindow.Initialize(context, ownerWindowHandle);
        }
        catch (Exception ex)
        {
            LogException("premium_store_context_window_initialize_failed", ex);
        }

        return context;
    }

    private void Publish(AppEntitlementSnapshot snapshot)
    {
        EventHandler<AppEntitlementSnapshot>? handler;
        lock (_sync)
        {
            _currentSnapshot = snapshot;
            handler = SnapshotChanged;
        }

        handler?.Invoke(this, snapshot);
    }

    private PremiumPurchaseResult MapPurchaseResult(StorePurchaseResult result, string productTitle)
    {
        return result.Status switch
        {
            StorePurchaseStatus.Succeeded => new PremiumPurchaseResult(
                PremiumPurchaseStatus.Succeeded,
                $"{productTitle} was unlocked successfully."),
            StorePurchaseStatus.AlreadyPurchased => new PremiumPurchaseResult(
                PremiumPurchaseStatus.AlreadyOwned,
                $"{productTitle} is already unlocked."),
            StorePurchaseStatus.NotPurchased => new PremiumPurchaseResult(
                PremiumPurchaseStatus.Canceled,
                $"{productTitle} purchase was canceled."),
            StorePurchaseStatus.NetworkError => new PremiumPurchaseResult(
                PremiumPurchaseStatus.NetworkError,
                $"Microsoft Store could not complete the {productTitle} purchase because of a network error."),
            StorePurchaseStatus.ServerError => new PremiumPurchaseResult(
                PremiumPurchaseStatus.ServerError,
                $"Microsoft Store could not complete the {productTitle} purchase because of a server error."),
            _ => new PremiumPurchaseResult(
                PremiumPurchaseStatus.UnknownError,
                $"Microsoft Store returned an unexpected purchase status for {productTitle}."),
        };
    }

    private static bool ContainsKeyword(string? value, string keyword)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void Log(string eventName, string metadata)
    {
        _processLogService.Log("Premium", $"{eventName}; {metadata}");
    }

    private void LogException(string eventName, Exception ex)
    {
        _processLogService.LogException("Premium", eventName, ex);
    }
}
