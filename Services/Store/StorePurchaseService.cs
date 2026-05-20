namespace AudioScript.Services.Store;

public sealed class StorePurchaseService : IStorePurchaseService
{
    private readonly IEntitlementService _entitlementService;

    public StorePurchaseService(IEntitlementService entitlementService)
    {
        _entitlementService = entitlementService ?? throw new ArgumentNullException(nameof(entitlementService));
    }

    public Task<PremiumPurchaseResult> RequestPremiumPurchaseAsync(CancellationToken cancellationToken = default) =>
        _entitlementService.RequestPremiumPurchaseAsync(cancellationToken);
}
