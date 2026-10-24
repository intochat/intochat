using System.Text.Json;
using IntoChat.OS;
using IntoChat.OS.AI.Models;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter.Button;
using IntoChat.OS.Flutter.Chat;
using IntoChat.OS.Flutter.Text;
using Xunit;

namespace IntoChat.OS.Kernel.Tests.Unit;

// The Settings app's logic: the tab it opens on, unarchive under the trust rule, models after a key change, and the
// account page Delete account opens.
public sealed class OsSettingsLogicFacts
{
    private const string App = "b1/apps/settings";
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static AppSnapshot Self(bool edited = false) => new("settings", new("Settings", "Settings", [], [], []), "", [], [], [], "2.0.0",
        AppStatus.Live, [], [], Origin: new("os", new AppSource.Shipped(), "settings", "2.0.0"), Edited: edited);

    private static (SettingsCommands Commands, FakeText State, FakeIndex Index) World(bool edited = false)
    {
        var state = new FakeText();
        var index = new FakeIndex();
        index.Add("c-1", "Trip to Lisbon", Now.AddHours(-1), archived: true);
        index.Add("c-2", "Groceries", Now.AddHours(-2), archived: true);
        return (new SettingsCommands(state, () => Task.FromResult(Self(edited)), () => Task.FromResult<IChatIndex>(index)), state, index);
    }

    [Theory]
    [InlineData("keys", "keys")]
    [InlineData("agents", "models")]
    [InlineData("", "general")]
    [InlineData("archived", "archived")]
    public async Task OpenWithATabInputSelectsThatTab(string input, string tab)
    {
        var (commands, state, _) = World();
        await commands.Command("tab:danger")!();
        await commands.Command("delete")!();
        await SettingsCommands.Open(state, input);
        var shown = await Settings.ReadState(state);
        Assert.Equal(tab, shown.Tab);
        Assert.False(shown.ConfirmDelete);
    }

    [Fact]
    public async Task UnarchiveRestoresTheRowAndToastsChatRestored()
    {
        var (commands, state, index) = World();
        await commands.Command("tab:archived")!();
        await commands.Command("unarchive:1")!();
        Assert.False(index.Entries["c-2"].Archived);
        Assert.True(index.Entries["c-1"].Archived);
        Assert.Equal(SettingsCopy.ChatRestored, (await Settings.ReadState(state)).Toast);
        // A row index past the page is a stale click.
        await commands.Command("unarchive:9")!();
        Assert.True(index.Entries["c-1"].Archived);
    }

    [Fact]
    public async Task AnEditedSettingsWritesNothing()
    {
        var (commands, state, index) = World(edited: true);
        var before = (await state.Read()).Version;
        await commands.Command("unarchive:0")!();
        Assert.True(index.Entries["c-1"].Archived);
        Assert.True(index.Entries["c-2"].Archived);
        Assert.Equal(before, (await state.Read()).Version);
        Assert.False(Settings.MayWrite(Self(edited: true)));
        Assert.True(Settings.MayWrite(Self()));
    }

    [Fact]
    public async Task UnknownAndCopyActionsAreIgnored()
    {
        var (commands, _, _) = World();
        Assert.Null(commands.Command("copy:https://example.test/x"));
        Assert.Null(commands.Command("tab:billing"));
        Assert.Null(commands.Command("refresh"));
        Assert.Null(commands.Command("unarchive:x"));
    }

    [Fact]
    public async Task AKeyChangeRefreshesTheModelList()
    {
        Assert.True(Settings.IsKeyControl(App, App + "/keys/openai"));
        Assert.False(Settings.IsKeyControl(App, App + "/variables"));
        Assert.False(Settings.IsKeyControl(App, "b2/apps/settings/keys/openai"));
        var keyed = false;
        var sources = new SettingsSources(
            () => Task.FromResult(new ModelChoices(new("auto", "Automatic", null, null, true, []),
                [new("preset:Big", "Big", "OpenAI", "big", keyed, [])])),
            (_, _) => Task.FromResult(new ChatIndexPage([], 0)),
            () => Task.FromResult<ConnectionRow[]>([]));
        var state = new SettingsViewState("models");
        Assert.False(Assert.Single((await Settings.Model(App, state, sources)).Models).Available);
        keyed = true;
        var after = await Settings.Model(App, state, sources);
        Assert.True(Assert.Single(after.Models).Available);
        Assert.All(SettingsView.ModelChoices(after), choice => Assert.True(choice.Enabled));
    }

    [Fact]
    public async Task DeleteAccountUsesThePublicOrigin()
    {
        var json = Settings.DeleteActivation("https://os.example.test/");
        Assert.Equal("https://os.example.test/account/profile", JsonDocument.Parse(json!).RootElement.GetProperty("url").GetString());
        var button = new FakeButton();
        await Settings.ArmDelete(button, "https://os.example.test");
        Assert.Equal(json, button.State.Activation);
        Assert.True(button.State.Enabled);
        Assert.Equal(SettingsCopy.OpenAccountPage, button.State.Label);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("javascript:alert(1)")]
    public async Task NoPublicOriginDisablesDeleteAccount(string? origin)
    {
        Assert.Null(Settings.DeleteActivation(origin));
        var button = new FakeButton();
        await Settings.ArmDelete(button, origin);
        Assert.False(button.State.Enabled);
        Assert.Null(button.State.Activation);
    }

    [Fact]
    public void ConnectionRowsListConnectionsThenOffers()
    {
        var rows = Settings.Connections(
            [new("gmail", "work", "gmail/work", "connected", "me@example.test", null)],
            [new("gmail", "Gmail"), new("salesforce", "Salesforce")],
            [new("gmail", "Gmail", true, true), new("salesforce", "Salesforce", true, false), new("hubspot", "HubSpot", false, false)]);
        Assert.Equal(
            [("gmail", "Gmail · me@example.test", "Connected", true, true), ("salesforce", "Salesforce", "Not connected", false, true),
             ("hubspot", "HubSpot", "Not available on this host.", false, false)],
            rows.Select(row => (row.Connector, row.Title, row.Status, row.Connected, row.Connectable)));
        Assert.Equal("{\"kind\":\"connections\",\"brainId\":\"b1\",\"connector\":\"gmail\"}", Settings.ConnectActivation("b1", "gmail"));
    }

    [Fact]
    public void TheChatIndexKeyFollowsChatDataApp()
    {
        var ino = Self() with { Settings = [] };
        Assert.Equal("b1/apps/ino/chats", Settings.ChatIndexKey("b1", ino));
        Assert.Equal("b1/apps/assistant/chats", Settings.ChatIndexKey("b1", ino with { Settings = new() { ["ChatDataApp"] = "b1/apps/assistant" } }));
    }

    private abstract class FakeNeuron : INeuron
    {
        public Task<Guid> Watch(INeuronObserver observer) => throw new NotSupportedException();
        public Task<Guid> Watch(INeuronObserver observer, string[] signals) => throw new NotSupportedException();
        public Task Unwatch(INeuronObserver observer) => throw new NotSupportedException();
        public Task<SignalPage> Read(long since, int limit) => throw new NotSupportedException();
    }

    private sealed class FakeText : FakeNeuron, IText
    {
        private TextState _state = new();
        public Task Set(string markdown) { _state = new() { Version = _state.Version + 1, Markdown = markdown }; return Task.CompletedTask; }
        public async Task<bool> TrySet(string markdown, int expectedVersion)
        {
            if (_state.Version != expectedVersion) { return false; }
            await Set(markdown);
            return true;
        }
        public Task<TextState> Read() => Task.FromResult(new TextState { Version = _state.Version, Markdown = _state.Markdown });
    }

    private sealed class FakeButton : FakeNeuron, IButton
    {
        public ButtonState State { get; } = new();
        public Task Set(string label, string action, bool enabled = true)
        { (State.Label, State.Action, State.Enabled, State.Activation) = (label, action, enabled, null); return Task.CompletedTask; }
        public Task SetActivation(string label, string activationJson)
        { (State.Label, State.Action, State.Enabled, State.Activation) = (label, "activate", true, activationJson); return Task.CompletedTask; }
        public Task SetVisual(ControlVisual visual) => throw new NotSupportedException();
        public Task Click() => throw new NotSupportedException();
        public Task ClickCurrent(string expectedAction) => throw new NotSupportedException();
        public Task<ButtonState> Read() => Task.FromResult(State);
    }

    private sealed class FakeIndex : FakeNeuron, IChatIndex
    {
        public Dictionary<string, ChatIndexEntry> Entries { get; } = [];
        public void Add(string id, string title, DateTimeOffset at, bool archived) => Entries[id] = new(id, title, at, archived);
        public Task<ChatIndexPage> List(bool archived, int skip = 0, int take = 100)
        {
            ChatIndexEntry[] all = [.. Entries.Values.Where(entry => entry.Archived == archived).OrderByDescending(entry => entry.UpdatedAt)];
            return Task.FromResult(new ChatIndexPage([.. all.Skip(skip).Take(take)], all.Length));
        }
        public Task<ChatIndexEntry> Create(string id, string title) => throw new NotSupportedException();
        public Task Rename(string id, string title) => throw new NotSupportedException();
        public Task Archive(string id) => throw new NotSupportedException();
        public Task Unarchive(string id) { Entries[id] = Entries[id] with { Archived = false }; return Task.CompletedTask; }
        public Task Touch(string id, string? firstMessage) => throw new NotSupportedException();
        public Task SetModel(string id, string? modelChoice) => throw new NotSupportedException();
        public Task<ChatIndexEntry?> Get(string id) => Task.FromResult(Entries.GetValueOrDefault(id));
    }
}
