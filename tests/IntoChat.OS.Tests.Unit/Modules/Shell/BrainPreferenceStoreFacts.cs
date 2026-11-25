using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Settings;
using TestOS = DigitalBrain.Testing.TestOS;

namespace IntoChat.OS.Modules.Flutter;

// The brain's notification switches and new-tab app live in its settings (IUserSettings). A brain that stored them as text fields
// under {brain}/settings before the move keeps them: they are adopted once, on the first read.
public sealed class BrainPreferenceStoreFacts
{
    private const string Scope = "owner";

    private static readonly BrainId Owner = new(Scope);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TestOS Started() => TestOS.Local().Register(typeof(FlutterModule).Assembly).Register<UserSettingsNeuron>();

    private static IUserSettings Settings(TestOS os) => os.Get<IUserSettings>(new NeuronId(Owner, "os.settings", IUserSettings.DefaultKey));

    // A text field as the earlier version stored it at {brain}/settings/....
    private static void SeedField(TestOS os, string key, string value)
    {
        var last = key.LastIndexOf('/');
        os.SeedStoredState(new NeuronId(Owner, "ui.field", key),
            $$"""{"owner":"{{Scope}}/{{key[..last]}}","name":"{{key[(last + 1)..]}}","kind":"Text","value":"{{value}}","version":2}""");
    }

    [Fact]
    public async Task A_brain_with_preferences_stored_as_text_fields_keeps_them_after_the_move()
    {
        using var person = Edge.AsPerson();
        await using var os = Started();
        SeedField(os, "settings/notifications/agent-finished", "off");
        SeedField(os, "settings/notifications/product-updates", "on");
        SeedField(os, "settings/workspace/new-tab-app", "notes");

        var read = await BrainPreferenceStore.ReadNotifications(new Brain(os), Scope, Ct);

        Assert.Equal(new Dictionary<string, bool>
        {
            ["agent-finished"] = false,
            ["needs-approval"] = true,
            ["connection-errors"] = true,
            ["product-updates"] = true,
        }, read);
        var settings = await Settings(os).Read();
        Assert.Equal([new NotificationSetting("agent-finished", false), new NotificationSetting("product-updates", true)], settings.Notifications!);
        Assert.Equal("notes", settings.NewTabApp);
    }

    [Fact]
    public async Task Adoption_runs_once_and_a_later_write_wins_over_the_stored_field()
    {
        using var person = Edge.AsPerson();
        await using var os = Started();
        SeedField(os, "settings/notifications/needs-approval", "off");
        var brain = new Brain(os);

        await BrainPreferenceStore.WriteNotifications(brain, Scope, new Dictionary<string, bool> { ["connection-errors"] = false }, Ct);
        await BrainPreferenceStore.WriteNotifications(brain, Scope, new Dictionary<string, bool> { ["needs-approval"] = true }, Ct);

        var read = await BrainPreferenceStore.ReadNotifications(brain, Scope, Ct);
        Assert.True(read["needs-approval"]);
        Assert.False(read["connection-errors"]);
    }

    [Fact]
    public async Task A_brain_with_nothing_stored_reads_the_defaults()
    {
        using var person = Edge.AsPerson();
        await using var os = Started();

        var read = await BrainPreferenceStore.ReadNotifications(new Brain(os), Scope, Ct);

        Assert.Equal(BrainPreferenceStore.NotificationKeys.ToDictionary(known => known.Key, known => known.Default), read);
        Assert.Equal("", (await Settings(os).Read()).NewTabApp);
    }

    [Fact]
    public async Task The_legacy_reader_answers_only_preferences_never_an_app_fields_draft()
    {
        using var person = Edge.AsPerson();
        await using var os = Started();
        os.SeedStoredState(new NeuronId(Owner, "ui.field", "apps/tasks/title"), """{"owner":"owner/apps/tasks","name":"title","kind":"Text","value":"draft","version":2}""");

        Assert.Null(await os.Get<ILegacyPreferenceField>(new NeuronId(Owner, "ui.field", "apps/tasks/title")).Value());
    }

    private sealed class Brain(TestOS os) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class => os.Get<T>(id);
    }
}
