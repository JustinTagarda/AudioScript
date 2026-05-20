namespace AudioScript.Services.Store;

public sealed class StoreLicenseService : IStoreLicenseService
{
    private readonly IEntitlementService _entitlementService;

    public StoreLicenseService(IEntitlementService entitlementService)
    {
        _entitlementService = entitlementService ?? throw new ArgumentNullException(nameof(entitlementService));
        _entitlementService.SnapshotChanged += OnSnapshotChanged;
    }

    public AppEntitlementSnapshot CurrentSnapshot => _entitlementService.CurrentSnapshot;

    public event EventHandler<AppEntitlementSnapshot>? SnapshotChanged;

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        _entitlementService.RefreshAsync(cancellationToken);

    private void OnSnapshotChanged(object? sender, AppEntitlementSnapshot snapshot)
    {
        SnapshotChanged?.Invoke(this, snapshot);
    }
}
