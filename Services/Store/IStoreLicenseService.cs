namespace AudioScript.Services.Store;

public interface IStoreLicenseService
{
    AppEntitlementSnapshot CurrentSnapshot { get; }

    event EventHandler<AppEntitlementSnapshot>? SnapshotChanged;

    Task RefreshAsync(CancellationToken cancellationToken = default);
}
