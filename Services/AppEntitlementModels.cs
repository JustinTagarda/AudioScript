using Windows.Services.Store;
using AudioScript.Services.Store;

namespace AudioScript.Services;

public sealed record AppEntitlementSnapshot(
    bool IsPackaged,
    bool HasPremium,
    bool IsPremiumProductAvailable,
    string PremiumProductDisplayName,
    string StatusMessage,
    PremiumEntitlementState State = PremiumEntitlementState.Checking,
    DateTimeOffset? LastVerifiedPremiumUtc = null,
    bool IsUsingGracePremium = false)
{
    public static AppEntitlementSnapshot Development(string premiumProductDisplayName) =>
        new(
            IsPackaged: false,
            HasPremium: true,
            IsPremiumProductAvailable: false,
            PremiumProductDisplayName: premiumProductDisplayName,
            StatusMessage: "Development build: Premium features unlocked.",
            State: PremiumEntitlementState.VerifiedPremium);
}

public enum PremiumEntitlementState
{
    Checking,
    VerifiedPremium,
    VerifiedBasic,
    VerificationInconclusive,
    VerificationFailed,
}

public enum PremiumPurchaseStatus
{
    Succeeded,
    AlreadyOwned,
    Canceled,
    Failed,
    NetworkError,
    ServerError,
    NotSupported,
    Blocked,
}

public sealed record PremiumPurchaseResult(
    PremiumPurchaseStatus Status,
    string Message);

public sealed class StoreEntitlementServiceOptions
{
    public bool TreatUnpackagedBuildsAsPremium { get; init; }

    public string PremiumProductDisplayName { get; init; } = "AudioScript Premium";

    public IReadOnlyList<string> PremiumStoreIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> PremiumProductIds { get; init; } = Array.Empty<string>();

    public string PremiumKeyword { get; init; } = "premium";

    public int RefreshRetryCount { get; init; } = 3;

    public TimeSpan[] RefreshRetryDelays { get; init; } = [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(7),
    ];

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
    private readonly IStoreContextProvider? _storeContextProvider;
    private readonly IPremiumEntitlementCache? _premiumEntitlementCache;
    private readonly StoreEntitlementServiceOptions _options;
    private readonly object _sync = new();
    private AppEntitlementSnapshot _currentSnapshot;
    private DateTimeOffset? _lastVerifiedPremiumUtc;

    public StoreEntitlementService(
        IAppVersionProvider appVersionProvider,
        ProcessLogService processLogService,
        Func<IntPtr>? ownerWindowHandleProvider = null,
        IStoreContextProvider? storeContextProvider = null,
        IPremiumEntitlementCache? premiumEntitlementCache = null,
        StoreEntitlementServiceOptions? options = null)
    {
        _appVersionProvider = appVersionProvider ?? throw new ArgumentNullException(nameof(appVersionProvider));
        _processLogService = processLogService ?? throw new ArgumentNullException(nameof(processLogService));
        _ownerWindowHandleProvider = ownerWindowHandleProvider;
        _storeContextProvider = storeContextProvider;
        _premiumEntitlementCache = premiumEntitlementCache;
        _options = options ?? new StoreEntitlementServiceOptions();
        _lastVerifiedPremiumUtc = _premiumEntitlementCache?.ReadLastVerifiedPremiumUtc();
        if (_appVersionProvider.IsPackaged && _options.PremiumStoreIds.Count != 1)
        {
            const string message =
                "Exactly one Premium Store add-on ID is required for packaged builds. Configure StoreEntitlementServiceOptions.PremiumStoreIds.";
            _processLogService.Log("Premium", $"configuration_error; {message}");
            throw new InvalidOperationException(message);
        }

        if (_appVersionProvider.IsPackaged && _options.PremiumProductIds.Count != 1)
        {
            const string message =
                "Exactly one Premium Product ID is required for packaged builds. Configure StoreEntitlementServiceOptions.PremiumProductIds.";
            _processLogService.Log("Premium", $"configuration_error; {message}");
            throw new InvalidOperationException(message);
        }

        _currentSnapshot = _appVersionProvider.IsPackaged || !_options.TreatUnpackagedBuildsAsPremium
            ? new AppEntitlementSnapshot(
                IsPackaged: _appVersionProvider.IsPackaged,
                HasPremium: false,
                IsPremiumProductAvailable: false,
                PremiumProductDisplayName: _options.PremiumProductDisplayName,
                StatusMessage: "Checking Microsoft Store entitlement...",
                State: PremiumEntitlementState.Checking)
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

        return RefreshPackagedWithRetryAsync(cancellationToken);
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
                PremiumPurchaseStatus.NotSupported,
                "Premium purchase is available only in the Microsoft Store version.");
        }

        if (IsProcessElevated())
        {
            return new PremiumPurchaseResult(
                PremiumPurchaseStatus.Blocked,
                "Close the app and reopen it normally. Microsoft Store purchase is unavailable while running as administrator.");
        }

        StoreContext context = CreateStoreContext();
        PremiumProductResolution resolution = await ResolvePremiumProductAsync(context, cancellationToken).ConfigureAwait(true);
        StoreProduct? premiumProduct = resolution.Product;
        if (premiumProduct is null)
        {
            Publish(CurrentSnapshot with
            {
                HasPremium = false,
                IsPremiumProductAvailable = false,
                StatusMessage = $"{_options.PremiumProductDisplayName} is not currently available in Microsoft Store.",
            });
            return new PremiumPurchaseResult(
                PremiumPurchaseStatus.Failed,
                "Premium purchase failed.");
        }

        try
        {
            string addOnStoreId = _options.PremiumStoreIds[0];
            StorePurchaseResult result = await context.RequestPurchaseAsync(addOnStoreId).AsTask(cancellationToken);
            PremiumPurchaseResult purchaseResult = MapPurchaseResult(result, premiumProduct.Title);
            if (purchaseResult.Status is PremiumPurchaseStatus.Succeeded or PremiumPurchaseStatus.AlreadyOwned)
            {
                await RefreshPackagedWithRetryAsync(cancellationToken).ConfigureAwait(true);
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
                PremiumPurchaseStatus.Failed,
                "Premium purchase failed.");
        }
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private async Task RefreshPackagedWithRetryAsync(CancellationToken cancellationToken)
    {
        Publish(CurrentSnapshot with
        {
            State = PremiumEntitlementState.Checking,
            StatusMessage = "Checking Microsoft Store entitlement...",
        });

        int attempt = 0;
        int retryCount = Math.Max(1, _options.RefreshRetryCount);
        while (attempt < retryCount)
        {
            try
            {
                attempt++;
                StoreContext context = CreateStoreContext();
                StoreAppLicense license = await context.GetAppLicenseAsync().AsTask(cancellationToken);
                PremiumLicenseResolution licenseResolution = ResolvePremiumLicense(license);
                PremiumProductResolution resolution = await ResolvePremiumProductAsync(context, cancellationToken).ConfigureAwait(false);
                StoreProduct? premiumProduct = resolution.Product;
                bool hasPremium = licenseResolution.License is not null || resolution.IsInUserCollection;
                if (hasPremium)
                {
                    _lastVerifiedPremiumUtc = DateTimeOffset.UtcNow;
                    _premiumEntitlementCache?.SaveVerifiedPremium(_lastVerifiedPremiumUtc.Value);
                }
                else
                {
                    _lastVerifiedPremiumUtc = null;
                    _premiumEntitlementCache?.Clear();
                }

                string productName = string.IsNullOrWhiteSpace(premiumProduct?.Title)
                    ? _options.PremiumProductDisplayName
                    : premiumProduct!.Title;
                bool hasConfiguredIds = _options.PremiumStoreIds.Any(id => !string.IsNullOrWhiteSpace(id));
                bool isInconclusive = hasConfiguredIds && premiumProduct is null;
                PremiumEntitlementState state = hasPremium
                    ? PremiumEntitlementState.VerifiedPremium
                    : isInconclusive
                        ? PremiumEntitlementState.VerificationInconclusive
                        : PremiumEntitlementState.VerifiedBasic;
                string statusMessage = hasPremium
                    ? $"{productName} is unlocked."
                    : state == PremiumEntitlementState.VerificationInconclusive
                        ? $"Unable to confidently map {_options.PremiumProductDisplayName} SKU in Microsoft Store right now."
                        : premiumProduct is null
                            ? $"{_options.PremiumProductDisplayName} is not currently available in Microsoft Store."
                            : $"{productName} is available for purchase in Microsoft Store.";

                Publish(new AppEntitlementSnapshot(
                    IsPackaged: true,
                    HasPremium: hasPremium,
                    IsPremiumProductAvailable: premiumProduct is not null,
                    PremiumProductDisplayName: productName,
                    StatusMessage: statusMessage,
                    State: state,
                    LastVerifiedPremiumUtc: _lastVerifiedPremiumUtc,
                    IsUsingGracePremium: false));
                Log(
                    "premium_entitlement_refreshed",
                    $"attempt={attempt}; state={state}; hasPremium={hasPremium}; productAvailable={premiumProduct is not null}; " +
                    $"productName='{productName}'; matchReason='{resolution.MatchReason}'; licenseMatchReason='{licenseResolution.MatchReason}'; " +
                    $"configuredStoreIds='{string.Join(",", _options.PremiumStoreIds)}'; configuredProductIds='{string.Join(",", _options.PremiumProductIds)}'; " +
                    $"storeProducts='{string.Join(",", resolution.CandidateStoreIds)}'; userCollection='{string.Join(",", resolution.UserCollectionStoreIds)}'; " +
                    $"licenseKeys='{string.Join(",", license.AddOnLicenses.Keys)}'; licenseDetails='{FormatLicenseDetails(license)}'.");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                bool shouldRetry = attempt < retryCount;
                LogException("premium_entitlement_refresh_failed", ex);
                if (shouldRetry)
                {
                    TimeSpan delay = ResolveRetryDelay(attempt - 1);
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                    continue;
                }

                bool allowGracePremium = _lastVerifiedPremiumUtc is not null;
                PremiumEntitlementState state = allowGracePremium
                    ? PremiumEntitlementState.VerificationInconclusive
                    : PremiumEntitlementState.VerificationFailed;
                Publish(new AppEntitlementSnapshot(
                    IsPackaged: true,
                    HasPremium: allowGracePremium,
                    IsPremiumProductAvailable: false,
                    PremiumProductDisplayName: _options.PremiumProductDisplayName,
                    StatusMessage: allowGracePremium
                        ? $"Using temporary {_options.PremiumProductDisplayName} access while Microsoft Store entitlement is being re-verified."
                        : $"Unable to verify {_options.PremiumProductDisplayName} entitlement right now.",
                    State: state,
                    LastVerifiedPremiumUtc: _lastVerifiedPremiumUtc,
                    IsUsingGracePremium: allowGracePremium));
                return;
            }
        }
    }

    private async Task<PremiumProductResolution> ResolvePremiumProductAsync(StoreContext context, CancellationToken cancellationToken)
    {
        string[] configuredStoreIds = GetConfiguredPremiumStoreIds();
        string[] configuredProductIds = GetConfiguredPremiumProductIds();
        string keyword = _options.PremiumKeyword?.Trim() ?? string.Empty;
        StoreProduct[] associatedDurableProducts = await QueryStoreProductsAsync(
            "associated_products",
            () => context.GetAssociatedStoreProductsAsync(new[] { "Durable" }).AsTask(cancellationToken)).ConfigureAwait(false);
        StoreProduct[] userCollectionProducts = await QueryStoreProductsAsync(
            "user_collection",
            () => context.GetUserCollectionAsync(new[] { "Durable" }).AsTask(cancellationToken)).ConfigureAwait(false);
        List<StoreProduct> candidateProducts = new(associatedDurableProducts);
        candidateProducts.AddRange(userCollectionProducts);
        string[] userCollectionStoreIds = userCollectionProducts.Select(product => product.StoreId).ToArray();

        if (configuredStoreIds.Length > 0)
        {
            StoreProduct[] configuredProducts = await QueryStoreProductsAsync(
                "configured_products",
                () => context.GetStoreProductsAsync(
                    new[] { "Durable" },
                    configuredStoreIds).AsTask(cancellationToken)).ConfigureAwait(false);
            candidateProducts.AddRange(configuredProducts);
            StoreProduct[] distinctProducts = candidateProducts
                .GroupBy(product => product.StoreId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            string[] configuredCandidateStoreIds = distinctProducts.Select(product => product.StoreId).ToArray();

            foreach (string configuredStoreId in configuredStoreIds)
            {
                StoreProduct? exact = distinctProducts.FirstOrDefault(product =>
                    MatchesProductStoreId(product.StoreId, configuredStoreId));
                if (exact is not null)
                {
                    return new PremiumProductResolution(
                        exact,
                        $"configured_store_id:{configuredStoreId}",
                        configuredCandidateStoreIds,
                        userCollectionStoreIds,
                        IsProductInUserCollection(exact, userCollectionStoreIds));
                }
            }
        }

        if (configuredProductIds.Length > 0)
        {
            StoreProduct[] distinctProducts = candidateProducts
                .GroupBy(product => product.StoreId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            string[] candidateStoreIdsForProductId = distinctProducts.Select(product => product.StoreId).ToArray();

            foreach (string configuredProductId in configuredProductIds)
            {
                StoreProduct? exact = distinctProducts.FirstOrDefault(product =>
                    string.Equals(product.InAppOfferToken, configuredProductId, StringComparison.OrdinalIgnoreCase));
                if (exact is not null)
                {
                    return new PremiumProductResolution(
                        exact,
                        $"configured_product_id:{configuredProductId}",
                        candidateStoreIdsForProductId,
                        userCollectionStoreIds,
                        IsProductInUserCollection(exact, userCollectionStoreIds));
                }
            }
        }

        StoreProduct[] durableProducts = candidateProducts
            .GroupBy(product => product.StoreId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        string[] candidateStoreIds = durableProducts.Select(product => product.StoreId).ToArray();

        if (!_appVersionProvider.IsPackaged && !string.IsNullOrWhiteSpace(keyword))
        {
            StoreProduct? keywordMatch = durableProducts.FirstOrDefault(product =>
                ContainsKeyword(product.Title, keyword)
                || ContainsKeyword(product.StoreId, keyword)
                || ContainsKeyword(product.InAppOfferToken, keyword));
            if (keywordMatch is not null)
            {
                return new PremiumProductResolution(
                    keywordMatch,
                    $"keyword_match:{keyword}",
                    candidateStoreIds,
                    userCollectionStoreIds,
                    IsProductInUserCollection(keywordMatch, userCollectionStoreIds));
            }
        }

        if (_appVersionProvider.IsPackaged)
        {
            return new PremiumProductResolution(null, "packaged_no_match", candidateStoreIds, userCollectionStoreIds, false);
        }

        if (durableProducts.Length == 1)
        {
            StoreProduct product = durableProducts[0];
            return new PremiumProductResolution(
                product,
                "single_durable_fallback",
                candidateStoreIds,
                userCollectionStoreIds,
                IsProductInUserCollection(product, userCollectionStoreIds));
        }

        return new PremiumProductResolution(null, "unpackaged_no_match", candidateStoreIds, userCollectionStoreIds, false);
    }

    private PremiumLicenseResolution ResolvePremiumLicense(StoreAppLicense license)
    {
        string[] configuredStoreIds = GetConfiguredPremiumStoreIds();
        string[] configuredProductIds = GetConfiguredPremiumProductIds();
        if (configuredStoreIds.Length == 0 && configuredProductIds.Length == 0)
        {
            return new PremiumLicenseResolution(null, "no_configured_store_or_product_ids");
        }

        foreach (string configuredStoreId in configuredStoreIds)
        {
            foreach (KeyValuePair<string, StoreLicense> item in license.AddOnLicenses)
            {
                if (!item.Value.IsActive)
                {
                    continue;
                }

                if (MatchesProductStoreIdOrSku(item.Key, configuredStoreId)
                    || MatchesProductStoreIdOrSku(item.Value.SkuStoreId, configuredStoreId))
                {
                    return new PremiumLicenseResolution(item.Value, $"license_sku_store_id:{configuredStoreId}");
                }
            }
        }

        foreach (KeyValuePair<string, StoreLicense> item in license.AddOnLicenses)
        {
            StoreLicense addOnLicense = item.Value;
            if (!addOnLicense.IsActive)
            {
                continue;
            }

            foreach (string configuredProductId in configuredProductIds)
            {
                if (string.Equals(addOnLicense.InAppOfferToken, configuredProductId, StringComparison.OrdinalIgnoreCase))
                {
                    return new PremiumLicenseResolution(addOnLicense, $"license_offer_token:{configuredProductId}");
                }
            }
        }

        return new PremiumLicenseResolution(null, "configured_license_not_found");
    }

    private string[] GetConfiguredPremiumStoreIds()
    {
        return _options.PremiumStoreIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string[] GetConfiguredPremiumProductIds()
    {
        return _options.PremiumProductIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<StoreProduct[]> QueryStoreProductsAsync(
        string queryName,
        Func<Task<StoreProductQueryResult>> query)
    {
        try
        {
            StoreProductQueryResult result = await query().ConfigureAwait(false);
            return (result.Products?.Values ?? Array.Empty<StoreProduct>()).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogException($"premium_{queryName}_query_failed", ex);
            return Array.Empty<StoreProduct>();
        }
    }

    private static bool IsProductInUserCollection(StoreProduct product, IReadOnlyList<string> userCollectionStoreIds)
    {
        return product.IsInUserCollection
            || userCollectionStoreIds.Any(storeId => MatchesProductStoreId(storeId, product.StoreId));
    }

    private static bool MatchesProductStoreIdOrSku(string? storeIdOrSkuStoreId, string productStoreId)
    {
        if (string.IsNullOrWhiteSpace(storeIdOrSkuStoreId))
        {
            return false;
        }

        return MatchesProductStoreId(storeIdOrSkuStoreId, productStoreId)
            || storeIdOrSkuStoreId.StartsWith($"{productStoreId}/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesProductStoreId(string? candidateStoreId, string productStoreId)
    {
        return !string.IsNullOrWhiteSpace(candidateStoreId)
            && string.Equals(candidateStoreId, productStoreId, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatLicenseDetails(StoreAppLicense license)
    {
        return string.Join(
            ",",
            license.AddOnLicenses.Select(item =>
                $"key={item.Key}|sku={item.Value.SkuStoreId}|offer={item.Value.InAppOfferToken}|active={item.Value.IsActive}"));
    }

    private StoreContext CreateStoreContext()
    {
        if (_storeContextProvider is not null)
        {
            return _storeContextProvider.GetContext();
        }

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

    private static bool IsProcessElevated()
    {
        try
        {
            using System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
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
                "Premium purchase canceled."),
            StorePurchaseStatus.NetworkError => new PremiumPurchaseResult(
                PremiumPurchaseStatus.NetworkError,
                "Premium purchase failed due to a network error. Check your connection and try again."),
            StorePurchaseStatus.ServerError => new PremiumPurchaseResult(
                PremiumPurchaseStatus.ServerError,
                "Microsoft Store could not complete the purchase right now. Try again later."),
            _ => new PremiumPurchaseResult(
                PremiumPurchaseStatus.Failed,
                "Premium purchase failed."),
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

    private TimeSpan ResolveRetryDelay(int attemptIndex)
    {
        if (_options.RefreshRetryDelays.Length == 0 || attemptIndex < 0)
        {
            return TimeSpan.Zero;
        }

        int index = Math.Min(attemptIndex, _options.RefreshRetryDelays.Length - 1);
        return _options.RefreshRetryDelays[index];
    }

    private sealed record PremiumProductResolution(
        StoreProduct? Product,
        string MatchReason,
        IReadOnlyList<string> CandidateStoreIds,
        IReadOnlyList<string> UserCollectionStoreIds,
        bool IsInUserCollection);

    private sealed record PremiumLicenseResolution(
        StoreLicense? License,
        string MatchReason);
}
