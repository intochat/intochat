using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Button;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Button;

// A button that installs, updates or removes an app runs with the person's session on one click, so only the
// Apps app the OS ships, in the button's own brain, may set one.
public sealed class AppActivationGuardFacts(FlutterHost host)
{
    [Fact]
    public async Task OnlyTheShippedAppsSetsAppActivations()
    {
        var ct = TestContext.Current.CancellationToken;
        using var person = await host.PersonAsync(ct: ct);
        var button = host.Brain.Get<IButton>(person.Key("apps/store-install"));
        string Install(string brain) => "{\"kind\":\"install\",\"brainId\":\"" + brain + "\",\"request\":{\"ref\":{\"publisher\":\"os\",\"name\":\"notes\"}}}";
        var own = Install(person.BrainId);

        async Task Refused(IDisposable caller, string activation)
        {
            using (caller)
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => button.SetActivation("Open notes", activation));
            }
        }

        await host.Brain.Get<IApp>(person.Key("apps/notes")).Create(new AppManifest("Notes", "Notes", [], [], []), "spec");
        await Refused(person.AsCaller(), own);
        await Refused(person.AsApp(person.Key("apps/notes")), own);
        foreach (var kind in new[] { "update", "remove", "Install" })
        {
            await Refused(person.AsApp(person.Key("apps/notes")), $$"""{"kind":"{{kind}}","brainId":"{{person.BrainId}}","name":"notes"}""");
        }
        var apps = host.Brain.Get<IApp>(person.Key("apps/apps"));
        await apps.Create(new AppManifest("Apps", "Apps", [], [], []), "spec");
        await Refused(person.AsApp(person.Key("apps/apps")), own);
        Assert.Null((await button.Read()).Activation);

        using (person.AsCaller()) { await apps.RecordOrigin(new(ShippedApps.Publisher, new AppSource.Shipped(), "apps", "1.0.0")); }
        await Refused(person.AsApp(person.Key("apps/apps")), Install("another-brain"));
        await Refused(person.AsApp(person.Key("apps/apps")), """{"kind":"remove","name":"notes"}""");
        using (person.AsApp(person.Key("apps/notes")))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => button.SetActivation("Open", "{\"kind\":\"open\",\"kind\":\"install\"}"));
        }
        using (person.AsApp(person.Key("apps/apps"))) { await button.SetActivation("Install Notes", own); }
        Assert.Equal(own, (await button.Read()).Activation);

        await apps.SaveManifest(new AppManifest("Apps", "edited", [], [], []), Guid.NewGuid());
        await Refused(person.AsApp(person.Key("apps/apps")), own);
        using (person.AsApp(person.Key("apps/notes"))) { await button.SetActivation("Open", """{"application":"notes"}"""); }
    }
}
