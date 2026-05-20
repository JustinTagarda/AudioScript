using Windows.Services.Store;

namespace AudioScript.Services.Store;

public interface IStoreContextProvider
{
    bool IsStoreApiAvailable { get; }

    StoreContext GetContext();
}
