using System.Reflection;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Entries;
using IntoChat.OS.Modules.Flutter.Entries.Signals;
using IntoChat.OS.Modules.Flutter.Tests.Integration.Settings;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Entries;

public sealed class EntryControlFacts(FlutterHost host)
{
    [Fact]
    public async Task KeySpecsNameOnlyKeyedProviders()
    {
        using var person = await host.PersonAsync(ct: TestContext.Current.CancellationToken);
        var control = host.Brain.Get<IEntryControl>(person.Key("settings/entry-key-openai"));
        await ShippedSettings.Install(host.Brain.Get<IApp>, person);
        using var settings = ShippedSettings.Caller(person);
        await control.Configure(new(EntryControlMode.Key, "OpenAI", "provider:openai"));
        var state = await control.Read();
        Assert.Equal("provider:openai", state.Spec!.EntryName);
        Assert.Equal(1, state.Version);
        foreach (var name in new string?[] { null, "", "provider:mistral", "OPENAI_KEY", "Provider:openai" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => control.Configure(new(EntryControlMode.Key, "OpenAI", name)));
        }
        await Assert.ThrowsAsync<ArgumentException>(() => control.Configure(new(EntryControlMode.Key, " ", "provider:openai")));
        await Assert.ThrowsAsync<ArgumentException>(() => control.Configure(new((EntryControlMode)7, "OpenAI", "provider:openai")));
        Assert.Equal(1, (await control.Read()).Version);
    }

    [Fact]
    public async Task CollectionSpecsNameNothing()
    {
        using var person = await host.PersonAsync(ct: TestContext.Current.CancellationToken);
        var control = host.Brain.Get<IEntryControl>(person.Key("settings/entry-collection"));
        await ShippedSettings.Install(host.Brain.Get<IApp>, person);
        using var settings = ShippedSettings.Caller(person);
        await control.Configure(new(EntryControlMode.Collection, "Variables and secrets"));
        Assert.Null((await control.Read()).Spec!.EntryName);
        await Assert.ThrowsAsync<ArgumentException>(() => control.Configure(new(EntryControlMode.Collection, "Variables", "API_BASE")));
        await Assert.ThrowsAsync<ArgumentException>(() => control.Configure(new(EntryControlMode.Collection, "Variables", "provider:openai")));
    }

    [Fact]
    public void StateNeverHoldsAValue()
    {
        static string[] Names(Type type) => [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order(StringComparer.Ordinal)];
        Assert.Equal(["Name", "Spec", "Version"], Names(typeof(EntryControlState)));
        Assert.Equal(["EntryName", "Label", "Mode"], Names(typeof(EntryControlSpec)));
        Assert.Equal(typeof(EntryControlSpec), typeof(EntryControlState).GetProperty("Spec")!.PropertyType);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ChangedPublishesOnlyTheNeuronName()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().WithNeuronModule<IntoChat.OS.Modules.Apps.AppsModule>()
            .ConfigureServices(services => global::DigitalBrain.Runtime.Orleans.BrainStorageHosting.AddJournalPayloads(services, typeof(IntoChat.OS.Modules.Apps.IApp).Assembly)).StartAsync(ct);
        var control = brain.Get<IEntryControl>(brain.Owner.Key("settings/keys-openai"));
        await ShippedSettings.Install(brain.Get<IApp>, brain.Owner);
        using (ShippedSettings.Caller(brain.Owner)) { await control.Configure(new(EntryControlMode.Key, "OpenAI", "provider:openai")); }
        await using var changes = await brain.Observe<EntryControlChanged>(control, ct);
        await control.Changed();
        var changed = await changes.NextAsync(ct: ct);
        Assert.EndsWith("keys-openai", changed.Name, StringComparison.Ordinal);
        Assert.Equal(["Name"], typeof(EntryControlChanged).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(p => p.Name));
    }

    [Fact]
    public async Task OnlyTheShippedSettingsConfiguresControls()
    {
        var ct = TestContext.Current.CancellationToken;
        using var person = await host.PersonAsync(ct: ct);
        await host.Brain.Get<IApp>(person.Key("apps/notes")).Create(new AppManifest("Notes", "Notes", [], [], []), "spec");
        var control = host.Brain.Get<IEntryControl>(person.Key("settings/entry-guard"));
        var spec = new EntryControlSpec(EntryControlMode.Key, "OpenAI", "provider:openai");

        async Task Refused(IDisposable caller)
        {
            using (caller)
            {
                var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => control.Configure(spec with { Label = "Misleading" }));
                Assert.Equal("Only the Settings app configures an entry control.", refused.Message);
            }
        }

        await Refused(person.AsCaller());
        await Refused(person.AsApp(person.Key("apps/notes")));
        await Refused(person.AsApp(""));
        await host.Brain.Get<IApp>(person.Key("apps/settings")).Create(new AppManifest("Settings", "Settings", [], [], []), "spec");
        await Refused(person.AsApp(person.Key("apps/settings")));
        Assert.Null((await control.Read()).Spec);

        var settings = await ShippedSettings.Install(host.Brain.Get<IApp>, person);
        using (person.AsApp(person.Key("apps/settings"))) { await control.Configure(spec); }
        await Refused(person.AsApp(person.Key("apps/notes")));
        await settings.SaveManifest(new AppManifest("Settings", "edited", [], [], []), Guid.NewGuid());
        await Refused(person.AsApp(person.Key("apps/settings")));
        var state = await control.Read();
        Assert.Equal("OpenAI", state.Spec!.Label);
        Assert.Equal(1, state.Version);
    }
}
