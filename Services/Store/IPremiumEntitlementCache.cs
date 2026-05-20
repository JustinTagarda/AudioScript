namespace AudioScript.Services.Store;

public interface IPremiumEntitlementCache
{
    DateTimeOffset? ReadLastVerifiedPremiumUtc();

    void SaveVerifiedPremium(DateTimeOffset verifiedUtc);

    void Clear();
}
