using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Apps;

// The Settings sideload operations as a value tree. The Flutter script keeps the rest of Settings.
public sealed class SideloadDesk(SideloadInstaller installer)
{
    public async Task<View> List(CancellationToken cancellationToken)
    {
        var rows = await installer.List(cancellationToken).ConfigureAwait(false);
        return new View("Sideload", new Collection([.. rows.Select(row => new Entry(row.Name, row.Title, row.Installed ? "installed" : "not installed"))]));
    }

    public async Task<View> Install(string? name, CancellationToken cancellationToken)
    {
        var result = await installer.Install(name, cancellationToken).ConfigureAwait(false);
        return new View("Sideload", new Text(result.Message));
    }

    public async Task<View> AddGit(string? url, CancellationToken cancellationToken)
    {
        var result = await installer.AddGit(url, cancellationToken).ConfigureAwait(false);
        return new View("Sideload", new Text(result.Message));
    }
}
