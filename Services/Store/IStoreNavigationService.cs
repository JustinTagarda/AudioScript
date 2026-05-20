namespace AudioScript.Services.Store;

public interface IStoreNavigationService
{
    bool CanOpenAppStorePage { get; }

    Task OpenAppStorePageAsync(CancellationToken cancellationToken = default);
}
