using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class StoreEntitlementServiceTests
{
    [Fact]
    public void Constructor_PackagedBuildWithoutPremiumStoreIds_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new StoreEntitlementService(
                new FakeVersionProvider(isPackaged: true),
                new ProcessLogService(),
                options: new StoreEntitlementServiceOptions
                {
                    PremiumStoreIds = Array.Empty<string>(),
                    PremiumProductIds = ["premium"],
                }));

        Assert.Contains("Premium Store add-on ID is required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_PackagedBuildWithoutPremiumProductIds_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new StoreEntitlementService(
                new FakeVersionProvider(isPackaged: true),
                new ProcessLogService(),
                options: new StoreEntitlementServiceOptions
                {
                    PremiumStoreIds = ["9PD5288V5Q49"],
                    PremiumProductIds = Array.Empty<string>(),
                }));

        Assert.Contains("Premium Product ID is required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_PackagedBuildWithMultiplePremiumStoreIds_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new StoreEntitlementService(
                new FakeVersionProvider(isPackaged: true),
                new ProcessLogService(),
                options: new StoreEntitlementServiceOptions
                {
                    PremiumStoreIds = ["9PD5288V5Q49", "duplicate"],
                    PremiumProductIds = ["audioscript_premium_lifetime"],
                }));

        Assert.Contains("Exactly one Premium Store add-on ID is required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_UnpackagedBuildWithoutPremiumStoreIds_DoesNotThrow()
    {
        var service = new StoreEntitlementService(
            new FakeVersionProvider(isPackaged: false),
            new ProcessLogService(),
            options: new StoreEntitlementServiceOptions
            {
                PremiumStoreIds = Array.Empty<string>(),
                PremiumProductIds = Array.Empty<string>(),
            });

        Assert.NotNull(service);
    }

    private sealed class FakeVersionProvider : IAppVersionProvider
    {
        public FakeVersionProvider(bool isPackaged)
        {
            IsPackaged = isPackaged;
        }

        public bool IsPackaged { get; }

        public string InstalledVersion => "1.0.0.0";
    }
}
