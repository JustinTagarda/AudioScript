using AudioScript.Services.Store;
using Xunit;

namespace AudioScript.Tests;

public sealed class StorePremiumAddonCatalogTests
{
    [Fact]
    public void AudioScriptPremiumLifetime_IsDurableForeverAndTargetsSameProduct()
    {
        StorePremiumAddonDefinition addon = StorePremiumAddonCatalog.AudioScriptPremiumLifetime;

        Assert.Equal("AudioScript Premium", addon.DisplayName);
        Assert.Equal("9PD5288V5Q49", addon.StoreId);
        Assert.Equal("audioscript_premium_lifetime", addon.ProductId);
        Assert.Equal(StoreAddonType.Durable, addon.AddonType);
        Assert.Equal(StoreAddonLifetime.Forever, addon.Lifetime);
        Assert.Equal(addon.ProductId, addon.PromoCodeTargetProductId);

        addon.Validate();
    }
}
