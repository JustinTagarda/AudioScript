namespace AudioScript.Services.Store;

public enum StoreAddonType
{
    Durable,
}

public enum StoreAddonLifetime
{
    Forever,
}

public sealed record StorePremiumAddonDefinition(
    string DisplayName,
    string StoreId,
    string ProductId,
    StoreAddonType AddonType,
    StoreAddonLifetime Lifetime,
    string PromoCodeTargetProductId)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            throw new InvalidOperationException("Premium add-on display name is required.");
        }

        if (string.IsNullOrWhiteSpace(StoreId))
        {
            throw new InvalidOperationException("Premium add-on Store ID is required.");
        }

        if (string.IsNullOrWhiteSpace(ProductId))
        {
            throw new InvalidOperationException("Premium add-on Product ID is required.");
        }

        if (AddonType != StoreAddonType.Durable)
        {
            throw new InvalidOperationException("Premium add-on must be configured as Durable.");
        }

        if (Lifetime != StoreAddonLifetime.Forever)
        {
            throw new InvalidOperationException("Premium add-on lifetime must be Forever.");
        }

        if (!string.Equals(ProductId, PromoCodeTargetProductId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Promo codes must target the same Premium add-on product.");
        }
    }
}

public static class StorePremiumAddonCatalog
{
    public static StorePremiumAddonDefinition AudioScriptPremiumLifetime { get; } =
        new(
            DisplayName: "AudioScript Premium",
            StoreId: "9PD5288V5Q49",
            ProductId: "audioscript_premium_lifetime",
            AddonType: StoreAddonType.Durable,
            Lifetime: StoreAddonLifetime.Forever,
            PromoCodeTargetProductId: "audioscript_premium_lifetime");
}
