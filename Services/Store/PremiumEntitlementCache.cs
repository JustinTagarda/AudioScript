using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AudioScript.Services.Store;

public sealed class PremiumEntitlementCache : IPremiumEntitlementCache
{
    private readonly string _cacheFilePath;
    private readonly ProcessLogService _processLogService;

    public PremiumEntitlementCache(string cacheFilePath, ProcessLogService processLogService)
    {
        _cacheFilePath = string.IsNullOrWhiteSpace(cacheFilePath)
            ? throw new ArgumentException("Premium cache path is required.", nameof(cacheFilePath))
            : cacheFilePath;
        _processLogService = processLogService ?? throw new ArgumentNullException(nameof(processLogService));
    }

    public DateTimeOffset? ReadLastVerifiedPremiumUtc()
    {
        try
        {
            if (!File.Exists(_cacheFilePath))
            {
                return null;
            }

            byte[] protectedBytes = File.ReadAllBytes(_cacheFilePath);
            byte[] bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            string text = Encoding.UTF8.GetString(bytes);
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset value)
                ? value.ToUniversalTime()
                : null;
        }
        catch (Exception ex)
        {
            _processLogService.LogException("Premium", "premium_cache_read_failed", ex);
            return null;
        }
    }

    public void SaveVerifiedPremium(DateTimeOffset verifiedUtc)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFilePath)!);
            byte[] bytes = Encoding.UTF8.GetBytes(verifiedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            byte[] protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_cacheFilePath, protectedBytes);
        }
        catch (Exception ex)
        {
            _processLogService.LogException("Premium", "premium_cache_write_failed", ex);
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_cacheFilePath))
            {
                File.Delete(_cacheFilePath);
            }
        }
        catch (Exception ex)
        {
            _processLogService.LogException("Premium", "premium_cache_clear_failed", ex);
        }
    }
}
