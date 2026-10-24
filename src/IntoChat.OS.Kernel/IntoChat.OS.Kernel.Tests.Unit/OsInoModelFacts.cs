using IntoChat.OS;
using IntoChat.OS.AI.Models;
using IntoChat.OS.Flutter.Chat;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.Views;
using Xunit;

namespace IntoChat.OS.Kernel.Tests.Unit;

// The per-chat model menu in Ino's header and the choice it stores.
public sealed class OsInoModelFacts
{
    private const string Chat = "c-11111111111111111111111111111111";

    private static ModelChoices Choices(bool autoAvailable = true) => new(
        new ModelChoice(null, "Automatic", null, null, autoAvailable, []) { Reason = autoAvailable ? null : "No model with tools" },
        [
            new("profile:fast", "Fast", "openai", "gpt-fast", true, ["tools"]),
            new("preset:Big", "Big", "anthropic", "big", false, ["tools"]) { Reason = "Add an Anthropic key" },
        ]);

    [Fact]
    public void AutoComesFirstWithItsCopy()
    {
        var menu = InoModels.Menu(Choices(autoAvailable: false), null);
        Assert.Equal((UiNodeKind.Menu, "model", "sparkle"), (menu.Kind, menu.Key, menu.Icon!));
        var auto = menu.Children[0];
        Assert.Equal(("model-auto", "Auto, cheapest model with tools", "model:auto"), (auto.Key, auto.Text!, auto.Action!));
        Assert.False(auto.Enabled);
        Assert.Equal("No model with tools", auto.Detail);
        Assert.Equal("Auto", menu.Text);
        Assert.Equal(UiStyle.Primary, auto.Style);
    }

    [Fact]
    public void UnavailableModelsAreDisabledWithTheirReason()
    {
        var menu = InoModels.Menu(Choices(), null);
        Assert.Equal(["model-auto", "model-0", "model-1"], menu.Children.Select(item => item.Key));
        var (fast, big) = (menu.Children[1], menu.Children[2]);
        Assert.Equal(("Fast", "model:profile:fast", true), (fast.Text!, fast.Action!, fast.Enabled));
        Assert.Null(fast.Detail);
        Assert.Equal(("Big", "model:preset:Big", false, "Add an Anthropic key"), (big.Text!, big.Action!, big.Enabled, big.Detail!));
    }

    [Fact]
    public void TheSelectedModelIsPrimaryAndLabelsTheMenu()
    {
        var menu = InoModels.Menu(Choices(), "profile:fast");
        Assert.Equal("Fast", menu.Text);
        Assert.Equal([UiStyle.Secondary, UiStyle.Primary, UiStyle.Secondary], menu.Children.Select(item => item.Style));
    }

    [Fact]
    public void AListedUnavailableSelectionSaysSoAndStaysMarked()
    {
        var menu = InoModels.Menu(Choices(), "preset:Big");
        Assert.Equal("Big (unavailable)", menu.Text);
        Assert.Equal([UiStyle.Secondary, UiStyle.Secondary, UiStyle.Primary], menu.Children.Select(item => item.Style));
    }

    [Fact]
    public void ARemovedSelectionGetsAMarkedDisabledStandIn()
    {
        var menu = InoModels.Menu(Choices(), "preset:Gone");
        Assert.Equal("preset:Gone (unavailable)", menu.Text);
        var stand = menu.Children[^1];
        Assert.Equal(("model-removed", "preset:Gone", false, UiStyle.Primary), (stand.Key, stand.Text!, stand.Enabled, stand.Style));
        Assert.DoesNotContain(menu.Children[..^1], item => item.Style == UiStyle.Primary);
    }

    [Fact]
    public async Task AnInheritedUnavailableSettingAnswersOnAutoWithANote()
    {
        var catalog = new FakeCatalog(Choices());
        var resolved = await InoModels.Resolve(catalog, null, "preset:Big");
        Assert.Equal(new InoModelResolution(InoModelOutcome.Ready, null, "The default model isn't available. Using Auto."), resolved);
        Assert.Null(InoModels.Shown(Choices(), null, "preset:Big"));
        Assert.Equal(new InoModelResolution(InoModelOutcome.Ready, new("profile:fast")), await InoModels.Resolve(catalog, null, "profile:fast"));
        Assert.Equal("profile:fast", InoModels.Shown(Choices(), null, "profile:fast"));
    }

    [Fact]
    public async Task AChatChosenUnavailableModelStartsNoTurn()
    {
        var catalog = new FakeCatalog(Choices());
        Assert.Equal(InoModelOutcome.ChatModelUnavailable, (await InoModels.Resolve(catalog, "preset:Big", "profile:fast")).Outcome);
        Assert.Equal(InoModelOutcome.ChatModelUnavailable, (await InoModels.Resolve(catalog, "preset:Gone", "")).Outcome);
        Assert.Equal("preset:Gone", InoModels.Shown(Choices(), "preset:Gone", "profile:fast"));
    }

    [Fact]
    public async Task ExplicitAutoWithoutAutomaticIsNoModel()
    {
        Assert.Equal(InoModelOutcome.NoModel, (await InoModels.Resolve(new FakeCatalog(Choices(autoAvailable: false)), "auto", "profile:fast")).Outcome);
        Assert.Equal(new InoModelResolution(InoModelOutcome.Ready), await InoModels.Resolve(new FakeCatalog(Choices()), "auto", "profile:fast"));
    }

    [Fact]
    public void EffectiveFallsBackToTheAppSetting()
    {
        Assert.Equal("gpt", InoModels.Effective(null, "gpt"));
        Assert.Null(InoModels.Effective("auto", "gpt"));
        Assert.Equal("m1", InoModels.Effective("m1", "gpt"));
        Assert.Null(InoModels.Effective(null, ""));
    }

    [Fact]
    public async Task ChoosingAnUnavailableModelChangesNothing()
    {
        var index = new FakeIndex();
        var status = new FakeText();
        var catalog = new FakeCatalog(Choices(autoAvailable: false));
        Assert.False(await InoModels.Choose(catalog, index, status, Chat, "model:preset:Big"));
        Assert.False(await InoModels.Choose(catalog, index, status, Chat, "model:auto"));
        Assert.False(await InoModels.Choose(catalog, index, status, Chat, "model:preset:Gone"));
        Assert.Null(index.Choice);
        Assert.Equal(InoModels.UnavailableCopy, (await status.Read()).Markdown);

        Assert.True(await InoModels.Choose(catalog, index, status, Chat, "model:profile:fast"));
        Assert.Equal("profile:fast", index.Choice);
        Assert.Equal("", (await status.Read()).Markdown);
    }

    private abstract class FakeNeuron : INeuron
    {
        public Task<Guid> Watch(INeuronObserver observer) => throw new NotSupportedException();
        public Task<Guid> Watch(INeuronObserver observer, string[] signals) => throw new NotSupportedException();
        public Task Unwatch(INeuronObserver observer) => throw new NotSupportedException();
        public Task<SignalPage> Read(long since, int limit) => throw new NotSupportedException();
    }

    private sealed class FakeCatalog(ModelChoices choices) : FakeNeuron, IModelCatalog
    {
        public Task<ModelChoices> List(string? defaultModel = null) => Task.FromResult(choices);
        public Task<ModelSelection?> Select(string? id, string? defaultModel = null)
            => Task.FromResult(choices.Models.Any(model => model.Id == id && model.Available) ? new ModelSelection(id) : null);
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

    private sealed class FakeIndex : FakeNeuron, IChatIndex
    {
        private ChatIndexEntry? _entry;
        public string? Choice => _entry?.ModelChoice;
        public Task<ChatIndexPage> List(bool archived, int skip = 0, int take = 100) => throw new NotSupportedException();
        public Task<ChatIndexEntry> Create(string id, string title) => Task.FromResult(_entry ??= new(id, title, DateTimeOffset.UnixEpoch, false));
        public Task Rename(string id, string title) => throw new NotSupportedException();
        public Task Archive(string id) => throw new NotSupportedException();
        public Task Unarchive(string id) => throw new NotSupportedException();
        public Task Touch(string id, string? firstMessage) => throw new NotSupportedException();
        public Task SetModel(string id, string? modelChoice)
        {
            _entry = (_entry ?? throw new KeyNotFoundException(id)) with { ModelChoice = modelChoice };
            return Task.CompletedTask;
        }
        public Task<ChatIndexEntry?> Get(string id) => Task.FromResult(_entry);
    }
}
