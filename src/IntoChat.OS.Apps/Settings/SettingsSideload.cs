using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Settings;

public static class SettingsSideload
{
    public static async Task<View> List(ISideload sideload)
    {
        ArgumentNullException.ThrowIfNull(sideload);
        var rows = await sideload.List().ConfigureAwait(false);
        return new View("Sideload", new Collection([.. rows.Select(row => new Entry(row.Name, row.Title, row.Installed ? "installed" : "not installed"))]));
    }

    public static async Task<View> Install(ISideload sideload, string? name, string? consent = null)
    {
        ArgumentNullException.ThrowIfNull(sideload);
        if (string.IsNullOrWhiteSpace(name))
        {
            return new View("Sideload", new Text("Name an app."));
        }
        var result = await sideload.Install(name, consent).ConfigureAwait(false);
        return new View("Sideload", new Text(result.Message));
    }

    public static async Task<View> AddGit(ISideload sideload, string? url)
    {
        ArgumentNullException.ThrowIfNull(sideload);
        if (string.IsNullOrWhiteSpace(url))
        {
            return new View("Sideload", new Text("Name a git URL."));
        }
        var result = await sideload.AddGit(url).ConfigureAwait(false);
        return new View("Sideload", new Text(result.Message));
    }
}
