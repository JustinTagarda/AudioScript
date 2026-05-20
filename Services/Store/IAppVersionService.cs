namespace AudioScript.Services.Store;

public interface IAppVersionService
{
    bool IsPackaged { get; }

    string VersionText { get; }
}
