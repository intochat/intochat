using DigitalBrain.Modules.Options;

namespace IntoChat.OS.Modules.Microsoft.Windows;

// MachineIdFile empty = %LOCALAPPDATA%\IntoChat.OS\machine-id; tests point it at a temp file.
public sealed class WindowsOptions : IModuleOptions
{
    public string MachineIdFile { get; set; } = "";

    public bool AutoEnrollThisPc { get; set; } = true;

    public string DefaultDownloadsPath { get; set; } = string.Empty;

    public string ResolveDefaultDownloadsPath()
    {
        if (!string.IsNullOrWhiteSpace(DefaultDownloadsPath))
        {
            return Path.GetFullPath(DefaultDownloadsPath);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile)
            ? string.Empty
            : Path.Combine(profile, "Downloads");
    }

    public void Validate() { }
}
