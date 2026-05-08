using AudioScript.Services;
using Xunit;

namespace AudioScript.Tests;

public sealed class StoreEntitlementServiceTests
{
    [Fact]
    public void Constructor_PackagedBuildWithoutPremiumStoreId_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new StoreEntitlementService(
                new FakeVersionProvider(isPackaged: true),
                new ProcessLogService(),
                options: new StoreEntitlementServiceOptions
                {
                    PremiumStoreId = string.Empty,
                }));

        Assert.Contains("Premium Store add-on ID is required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_UnpackagedBuildWithoutPremiumStoreId_DoesNotThrow()
    {
        var service = new StoreEntitlementService(
            new FakeVersionProvider(isPackaged: false),
            new ProcessLogService(),
            options: new StoreEntitlementServiceOptions
            {
                PremiumStoreId = string.Empty,
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

        public string DisplayVersionText => "1.0.0";
    }
}

