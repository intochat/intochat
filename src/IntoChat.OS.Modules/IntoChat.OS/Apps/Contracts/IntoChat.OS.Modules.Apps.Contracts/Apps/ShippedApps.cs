namespace IntoChat.OS.Modules.Apps;

// Whether an installed app is the program the OS ships under that name: shipped from a trusted
// publisher, recorded under the same name, and not edited since. An app that only bears the name
// (a person's own, a copy from another brain, an edited fork) is not, and gets no OS authority.
public static class ShippedApps
{
    // The publisher the OS ships its own apps as; hosts may trust more (OsAppsOptions.TrustedPublishers).
    public const string Publisher = "os";

    // The OS's own services: the desktop shows none of them as a window.
    public static readonly IReadOnlySet<string> Services = new HashSet<string>(["desktop", "workspace", "usage"], StringComparer.Ordinal);

    // What the OS runs on, which Remove refuses: its services, Settings, Apps, Ino and Creator.
    // Apps folded into Settings (08-09). The folder is no longer a boot root.
    public static readonly IReadOnlySet<string> Roots = new HashSet<string>([.. Services, "settings", "ino", "creator"], StringComparer.Ordinal);

    public static bool Is(AppSnapshot app, string name) => Is(app, name, [Publisher]);

    public static bool Is(AppSnapshot app, string name, IEnumerable<string> trustedPublishers)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Manifest.Title.Length > 0 && !app.Edited
            && app.Origin is { Source: AppSource.Shipped } origin && origin.Name == name
            && trustedPublishers.Contains(origin.Publisher, StringComparer.Ordinal);
    }
}
