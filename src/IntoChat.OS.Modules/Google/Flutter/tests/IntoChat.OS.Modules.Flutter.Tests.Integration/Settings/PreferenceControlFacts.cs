using System.Net.Http.Json;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Preferences;
using IntoChat.OS.Modules.Flutter.TextField;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Settings;

public sealed class PreferenceControlFacts(FlutterHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task Install(IntoChat.OS.Testing.Person person, string name, string title)
        => await host.Brain.Get<IApp>(IApp.Key(person.BrainId, name)).Create(new AppManifest(title, title, [], [], []), "spec");

    private async Task<IPreferenceControl> Control(IntoChat.OS.Testing.Person person, string name, PreferenceSpec spec)
    {
        var control = host.Brain.Get<IPreferenceControl>(person.Key("settings/" + name));
        await ShippedSettings.Install(key => host.Brain.Get<IApp>(key), person);
        using (ShippedSettings.Caller(person)) { await control.Configure(spec); }
        return control;
    }

    private static PreferenceSpec Toggle(PreferenceStore store) => new(store, "Label", PreferenceKind.Toggle);

    [Fact]
    public async Task MembersSetNotificationsAndNewTab()
    {
        using var person = await host.PersonAsync(ct: Ct);
        await Install(person, "ino", "Ino");
        await Install(person, "notes", "Notes");
        var finished = await Control(person, "pref-finished", Toggle(PreferenceStore.NotifyAgentFinished));
        var updates = await Control(person, "pref-updates", Toggle(PreferenceStore.NotifyProductUpdates));
        var tab = await Control(person, "pref-tab", new(PreferenceStore.NewTabApp, "New tab opens", PreferenceKind.Choice));

        using (person.AsCaller())
        {
            Assert.Equal("on", (await finished.Read()).Value);
            Assert.Equal("off", (await updates.Read()).Value);
            var before = await tab.Read();
            Assert.Equal("ino", before.Value);
            Assert.Equal(["ino", "notes", "settings"], before.Spec!.Choices!.Select(choice => choice.Id));

            await finished.Set("off");
            await updates.Set("on");
            await tab.Set("notes");
            await Assert.ThrowsAsync<ArgumentException>(() => finished.Set("maybe"));

            Assert.Equal("off", (await finished.Read()).Value);
            Assert.Equal("on", (await updates.Read()).Value);
            Assert.Equal("notes", (await tab.Read()).Value);
        }
        Assert.Equal("off", (await host.Brain.Get<ITextField>(person.BrainId + "/settings/notifications/agent-finished").Read()).Value);
        Assert.Equal("notes", (await host.Brain.Get<ITextField>(person.BrainId + "/settings/workspace/new-tab-app").Read()).Value);
    }

    [Fact]
    public async Task AppCallersCannotSetPreferences()
    {
        using var person = await host.PersonAsync(ct: Ct);
        await Install(person, "notes", "Notes");
        var finished = await Control(person, "pref-finished", Toggle(PreferenceStore.NotifyAgentFinished));
        var tab = await Control(person, "pref-tab", new(PreferenceStore.NewTabApp, "New tab opens", PreferenceKind.Choice));

        // An installed app's own stamp, and a call relayed by the app proxy with no app key of its own.
        foreach (var app in new[] { person.Key("apps/notes"), "" })
        {
            using (person.AsAppIn(person.BrainId, app))
            {
                var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => finished.Set("off"));
                Assert.Equal("Installed apps cannot change a person's preferences.", refused.Message);
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tab.Set("notes"));
            }
        }
        using (person.AsCaller())
        {
            Assert.Equal("on", (await finished.Read()).Value);
            Assert.Equal("ino", (await tab.Read()).Value);
        }
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("nope")]
    [InlineData("")]
    public async Task NewTabRejectsHiddenOrMissingApps(string name)
    {
        using var person = await host.PersonAsync(ct: Ct);
        await Install(person, "desktop", "Desktop");
        var tab = await Control(person, "pref-tab", new(PreferenceStore.NewTabApp, "New tab opens", PreferenceKind.Choice));

        using (person.AsCaller())
        {
            await Assert.ThrowsAsync<ArgumentException>(() => tab.Set(name));
            Assert.Equal("ino", (await tab.Read()).Value);
        }
        Assert.True(string.IsNullOrEmpty((await host.Brain.Get<ITextField>(person.BrainId + "/settings/workspace/new-tab-app").Read()).Value));
    }

    [Fact]
    public async Task LocalStoresAreNeverWrittenServerSide()
    {
        using var person = await host.PersonAsync(ct: Ct);
        foreach (var store in new[] { PreferenceStore.LocalDisplayName, PreferenceStore.LocalTheme, PreferenceStore.LocalCompactSpacing, PreferenceStore.LocalReducedMotion, PreferenceStore.LocalDefaultModel })
        {
            var control = await Control(person, "pref-" + (int)store, new(store, "Label", PreferenceKind.Text));
            using (person.AsCaller())
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => control.Set("fixture-value-0001"));
                var state = await control.Read();
                Assert.Null(state.Value);
                Assert.Equal(1, state.Version);
            }
        }
    }

    [Fact]
    public async Task TheHttpEndpointsAndTheNeuronShareOneStore()
    {
        using var person = await host.PersonAsync(ct: Ct);
        var errors = await Control(person, "pref-errors", Toggle(PreferenceStore.NotifyConnectionErrors));
        using (person.AsCaller()) { await errors.Set("off"); }

        var prefs = await person.Http.GetFromJsonAsync<Dictionary<string, bool>>($"/brains/{person.BrainId}/preferences/notifications", Ct);
        Assert.False(prefs!["connection-errors"]);

        using var put = await person.Http.PutAsJsonAsync($"/brains/{person.BrainId}/preferences/notifications",
            new Dictionary<string, bool> { ["connection-errors"] = true }, Ct);
        put.EnsureSuccessStatusCode();
        using (person.AsCaller()) { Assert.Equal("on", (await errors.Read()).Value); }
    }

    [Fact]
    public async Task SpecsMatchTheirStore()
    {
        using var person = await host.PersonAsync(ct: Ct);
        var control = host.Brain.Get<IPreferenceControl>(person.Key("settings/pref-bad"));
        await ShippedSettings.Install(key => host.Brain.Get<IApp>(key), person);
        using (ShippedSettings.Caller(person))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => control.Configure(new(PreferenceStore.NotifyAgentFinished, " ", PreferenceKind.Toggle)));
            await Assert.ThrowsAsync<ArgumentException>(() => control.Configure(new(PreferenceStore.NotifyAgentFinished, "Label", PreferenceKind.Choice)));
            await Assert.ThrowsAsync<ArgumentException>(() => control.Configure(new(PreferenceStore.NewTabApp, "Label", PreferenceKind.Toggle)));
            await Assert.ThrowsAsync<ArgumentException>(() => control.Configure(new((PreferenceStore)7, "Label", PreferenceKind.Toggle)));
            await Assert.ThrowsAsync<ArgumentException>(() => control.Configure(new(PreferenceStore.LocalTheme, "Label", (PreferenceKind)9)));
        }
    }

    [Fact]
    public async Task OnlyTheShippedSettingsConfiguresControls()
    {
        using var person = await host.PersonAsync(ct: Ct);
        await Install(person, "notes", "Notes");
        var control = host.Brain.Get<IPreferenceControl>(person.Key("settings/pref-guard"));
        var spec = Toggle(PreferenceStore.NotifyAgentFinished);

        async Task Refused(IDisposable caller)
        {
            using (caller)
            {
                var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => control.Configure(spec with { Label = "Misleading" }));
                Assert.Equal("Only the Settings app configures a preference control.", refused.Message);
            }
        }

        // The person directly, another installed app, the proxy with no app key, and a Settings that only bears the name.
        await Refused(person.AsCaller());
        await Refused(person.AsApp(person.Key("apps/notes")));
        await Refused(person.AsApp(""));
        await Install(person, "settings", "Settings");
        await Refused(person.AsApp(person.Key("apps/settings")));
        using (person.AsCaller()) { Assert.Null((await control.Read()).Spec); }

        var settings = await ShippedSettings.Install(key => host.Brain.Get<IApp>(key), person);
        using (person.AsApp(person.Key("apps/settings"))) { await control.Configure(spec); }
        await Refused(person.AsApp(person.Key("apps/notes")));

        // An edited Settings is the person's own program, no longer the one the OS ships.
        await settings.SaveManifest(new AppManifest("Settings", "edited", [], [], []), Guid.NewGuid());
        await Refused(person.AsApp(person.Key("apps/settings")));
        using (person.AsCaller())
        {
            var state = await control.Read();
            Assert.Equal("Label", state.Spec!.Label);
            Assert.Equal(1, state.Version);
        }
    }
}
