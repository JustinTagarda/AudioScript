namespace AudioScript.Services;

public static class ApplicationDeploymentInfo {
    public static Version CurrentVersion => typeof(ApplicationDeploymentInfo).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
}

