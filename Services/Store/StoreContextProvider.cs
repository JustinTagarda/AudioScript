using Windows.Foundation.Metadata;
using Windows.Services.Store;

namespace AudioScript.Services.Store;

public sealed class StoreContextProvider : IStoreContextProvider
{
    private readonly Func<IntPtr>? _ownerWindowHandleProvider;
    private readonly ProcessLogService _processLogService;

    public StoreContextProvider(ProcessLogService processLogService, Func<IntPtr>? ownerWindowHandleProvider = null)
    {
        _processLogService = processLogService ?? throw new ArgumentNullException(nameof(processLogService));
        _ownerWindowHandleProvider = ownerWindowHandleProvider;
    }

    public bool IsStoreApiAvailable =>
        ApiInformation.IsTypePresent("Windows.Services.Store.StoreContext");

    public StoreContext GetContext()
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
            _processLogService.LogException("Store", "store_context_window_initialize_failed", ex);
        }

        return context;
    }
}
