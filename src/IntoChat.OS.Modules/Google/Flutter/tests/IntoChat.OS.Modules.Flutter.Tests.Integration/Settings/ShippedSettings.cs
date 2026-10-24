using IntoChat.OS.Apps;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Settings;

// The Settings app the OS ships, installed in a person's brain, so facts can configure its controls as it does.
// RecordOrigin also clears Edited, so Install makes an edited Settings shipped again.
// Enter Caller after awaiting Install: a caller stamp entered inside an awaited method does not flow back out.
internal static class ShippedSettings
{
    public static async Task<IApp> Install(Func<string, IApp> apps, IntoChat.OS.Testing.Person person)
    {
        var settings = apps(IApp.Key(person.BrainId, "settings"));
        // As the person: an app behind the proxy may not record an origin.
        using var asPerson = person.AsCaller();
        if (!(await settings.Read()).Manifest.Title.Equals("Settings", StringComparison.Ordinal))
        {
            await settings.Create(new AppManifest("Settings", "Settings", [], [], []), "spec");
        }
        await settings.RecordOrigin(new(ShippedApps.Publisher, new AppSource.Shipped(), "settings", "2.0.0"));
        return settings;
    }

    public static IDisposable Caller(IntoChat.OS.Testing.Person person) => person.AsApp(IApp.Key(person.BrainId, "settings"));
}
