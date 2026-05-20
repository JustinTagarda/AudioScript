namespace AudioScript.Services.Store;

public interface IStorePurchaseService
{
    Task<PremiumPurchaseResult> RequestPremiumPurchaseAsync(CancellationToken cancellationToken = default);
}
