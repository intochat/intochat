using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Link;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Apps.Tests.Unit.Settings;

// Settings' preferences are control neurons in its view: a command reaches the control through the app's slot, the control signals, and
// the app follows the signal and stores the preference. Text reaches the app only through a form's submit.
public sealed class SettingsControlsFacts
{
    private static readonly BrainId Brain = new("local");

    private static async Task<InstalledApp<SettingsApp>> Settings(TestOS os, string section)
    {
        var settings = await os.Install<SettingsApp>();
        await settings.Request("navigate", new SettingsNavigate(section));
        return settings;
    }

    private static async Task Send(InstalledApp<SettingsApp> settings, string slot, Signal command)
    {
        var alias = command.GetType().GetCustomAttributes(typeof(SignalAttribute), false).Cast<SignalAttribute>().Single().Alias;
        using (settings.OS.Edge.Enter(settings.OS.Edge.Person("local", "local", Brain)))
        {
            await settings.Screen.Dispatch(new Command(slot, alias, JsonSerializer.SerializeToElement(command, command.GetType(), JsonSerializerOptions.Web)));
        }
        await settings.OS.Idle();
    }

    private static NeuronId Control(string type, string key)
        => new(Brain, type, ScreenExtensions.ChildAddress(new NeuronId(Brain, "app.settings", "default"), key));

    [Fact]
    public async Task Toggling_a_setting_through_its_ToggleSwitch_slot_updates_the_preference()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var settings = await Settings(os, "desktop");
        Assert.True(settings.App.State.ShowOsBarClock);

        await Send(settings, "bar-clock", new SetChecked(false));

        Assert.False(settings.App.State.ShowOsBarClock);
        await Send(settings, "bar-clock", new SetChecked(true));
        Assert.True(settings.App.State.ShowOsBarClock);
    }

    [Fact]
    public async Task The_switch_draws_the_preference_from_its_props()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var settings = await Settings(os, "notifications");

        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));
        var view = await settings.Screen.Read();
        var slot = Slots(view!.View).Single(node => node.GetProperty("neuronUri").GetString()!.EndsWith("/notify-approvals", StringComparison.Ordinal));

        Assert.True(slot.GetProperty("props").GetProperty("isOn").GetBoolean());
    }

    [Fact]
    public async Task A_toggle_updates_the_preference_without_anything_pushed_to_the_switch()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var settings = await Settings(os, "notifications");

        await Send(settings, "notify-jobs", new SetChecked(false));

        Assert.False(settings.App.State.NotifyBackgroundJobs);
        var signals = os.Journal(Brain).Signals(Control("control.toggleSwitch", "notify-jobs")).Select(entry => entry.Signal)
            .Where(signal => signal is not StateChanged and not ViewChanged).ToArray();
        Assert.Equal([new Toggled(false)], signals);
    }

    private static IEnumerable<JsonElement> Slots(JsonElement json) => json.ValueKind switch
    {
        JsonValueKind.Object => (json.TryGetProperty("refKind", out var kind) && kind.GetString() == "child" ? [json] : Enumerable.Empty<JsonElement>())
            .Concat(json.EnumerateObject().SelectMany(property => Slots(property.Value))),
        JsonValueKind.Array => json.EnumerateArray().SelectMany(Slots),
        _ => [],
    };

    [Fact]
    public async Task Choosing_a_bar_layout_through_its_ComboBox_slot_saves_it()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var settings = await Settings(os, "desktop");

        await Send(settings, "bar-layout", new DigitalBrain.UI.Commands.Select("compact-pill"));

        Assert.Equal("compact-pill", settings.App.State.BarLayout);
    }

    [Fact]
    public async Task Submitting_the_settings_form_stores_values()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var settings = await Settings(os, "general");

        await Send(settings, "regional", new Submit(new Dictionary<string, string>
        {
            ["displayName"] = "Ada",
            ["language"] = "Ukrainian",
            ["timeZone"] = "Europe/Kyiv",
            ["dateFormat"] = "2026-11-18",
        }));

        Assert.Equal("Ada", settings.App.State.DisplayName);
        Assert.Equal("Ukrainian", settings.App.State.Language);
        Assert.Equal("Europe/Kyiv", settings.App.State.TimeZone);
        Assert.Equal("2026-11-18", settings.App.State.DateFormat);
    }

    [Fact]
    public async Task Search_filters_only_on_submit()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var settings = await Settings(os, "general");

        await Send(settings, "search", new Submit(new Dictionary<string, string> { ["query"] = "Ollama" }));

        Assert.Equal("Ollama", settings.App.State.SearchQuery);
    }

    [Fact]
    public async Task A_managed_setting_is_drawn_disabled_and_refuses()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var settings = await os.Install<SettingsApp>();
        await settings.Request("set-variable", new SettingsSetVariable(SettingsControls.ManagedVariable, "quiet-hours"));
        await settings.Request("navigate", new SettingsNavigate("notifications"));

        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => Send(settings, "quiet-hours", new SetChecked(true)));

        Assert.Equal(ControlProps.Disabled, refused.Reason);
        Assert.False(settings.App.State.QuietHoursEnabled);
    }

    [Fact]
    public async Task The_API_keys_form_is_a_control_form_child_whose_secrets_arrive_only_as_vault_references()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var settings = await Settings(os, "keys");

        using (os.Edge.Enter(os.Edge.Person("local", "local", Brain)))
        {
            var view = await settings.Screen.Read();
            var form = Slots(view!.View).Single(node => node.GetProperty("neuronUri").GetString()!.EndsWith("/" + SettingsControls.ApiKeys, StringComparison.Ordinal));
            Assert.Contains("/control.form/", form.GetProperty("neuronUri").GetString()!, StringComparison.Ordinal);
            Assert.All(form.GetProperty("props").GetProperty("fields").EnumerateArray(), field => Assert.Equal("Secret", field.GetProperty("kind").GetString()));
            Assert.DoesNotContain("ui.form", view.View.GetRawText(), StringComparison.Ordinal);
        }

        const string key = "sk-ant-" + "raw-key-never-journaled";
        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(
            () => Send(settings, SettingsControls.ApiKeys, new Submit(new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = key })));
        Assert.Equal(FormRefusals.SecretInValues, refused.Reason);

        var reference = "secret://local/app.settings/default/api-keys/ANTHROPIC_API_KEY";
        await Send(settings, SettingsControls.ApiKeys, new Submit(new Dictionary<string, string>(), new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = reference }));

        Assert.Contains("1 API key", settings.App.State.Status, StringComparison.Ordinal);
        var journal = string.Join("\n", os.Journal(Brain).Signals(Control("control.form", SettingsControls.ApiKeys))
            .Concat(os.Journal(Brain).Signals(new NeuronId(Brain, "app.settings", "default")))
            .Select(entry => JsonSerializer.Serialize(entry.Signal, entry.Signal.GetType())));
        Assert.Contains(reference, journal, StringComparison.Ordinal);
        Assert.DoesNotContain(key, journal, StringComparison.Ordinal);
        Assert.DoesNotContain(key, JsonSerializer.Serialize(settings.App.State), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_form_value_over_its_limit_is_refused()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var settings = await Settings(os, "general");

        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(
            () => Send(settings, "regional", new Submit(new Dictionary<string, string> { ["displayName"] = new string('a', 65) })));

        Assert.Equal(FormRefusals.TooLong, refused.Reason);
        Assert.Equal("User", settings.App.State.DisplayName);
    }

    [Fact]
    public async Task TheAiSectionDrawsOneComboPerRole()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var settings = await Settings(os, "models");

        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));
        var view = await settings.Screen.Read();
        var comboSlotKeys = Slots(view!.View)
            .Where(node => node.GetProperty("neuronUri").GetString()!.Contains("/control.comboBox/", StringComparison.Ordinal))
            .Select(node =>
            {
                var uri = node.GetProperty("neuronUri").GetString()!;
                return uri[(uri.LastIndexOf('/') + 1)..];
            })
            .ToArray();

        Assert.Equal(Enum.GetValues<ModelRole>().Select(SettingsControls.RoleKey), comboSlotKeys);
    }

    [Fact]
    public async Task TheHearComboOffersOnlyHearCards()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var settings = await Settings(os, "models");

        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));
        var view = await settings.Screen.Read();
        var hearKey = SettingsControls.RoleKey(ModelRole.Hear);
        var hearSlot = Slots(view!.View).Single(node => node.GetProperty("neuronUri").GetString()!.EndsWith($"/{hearKey}", StringComparison.Ordinal));
        var options = hearSlot.GetProperty("props").GetProperty("options").EnumerateArray()
            .Select(o => o.GetProperty("id").GetString()!)
            .ToArray();

        Assert.NotEmpty(options);
        Assert.All(options, id => Assert.True(KnownModels.Find(id)!.Can.HasFlag(Abilities.Hear)));
    }

    [Fact]
    public async Task ChangingARoleComboSetsTheRole()
    {
        RecordingSettingsModelCatalog.Reset();
        try
        {
            await using var os = TestOS.Local()
                .RegisterCatalog()
                .Register<RecordingSettingsModelCatalog>();
            var settings = await Settings(os, "models");

            await Send(settings, SettingsControls.RoleKey(ModelRole.Think), new DigitalBrain.UI.Commands.Select("claude-opus-5"));

            Assert.NotNull(RecordingSettingsModelCatalog.LastSetRole);
            Assert.Equal((ModelRole.Think, "claude-opus-5"), (RecordingSettingsModelCatalog.LastSetRole!.Value.Role, RecordingSettingsModelCatalog.LastSetRole.Value.Model.Value));
        }
        finally
        {
            RecordingSettingsModelCatalog.Reset();
        }
    }

    [Fact]
    public void OldSettingsStateWithAvailableModelsStillLoads()
    {
        const string oldStateJsonWithAvailableModels = """
            {
              "title": "Settings",
              "status": "Settings",
              "displayName": "Ada",
              "theme": "dark",
              "defaultModel": "qwen3.5:4b",
              "section": "models",
              "availableModels": [
                { "id": "qwen3.5:4b", "label": "Qwen 3.5 4B", "available": true, "providerLabel": "Ollama (Local GPU)" }
              ]
            }
            """;

        var loaded = JsonSerializer.Deserialize<SettingsAppState>(oldStateJsonWithAvailableModels, JsonSerializerOptions.Web);

        Assert.NotNull(loaded);
        Assert.Equal("Ada", loaded.DisplayName);
        Assert.Equal("qwen3.5:4b", loaded.DefaultModel);
        Assert.Equal("models", loaded.Section);
    }

    [Fact]
    public async Task TheModelsSectionRendersAllowClientCodeSandboxToggleWithPlatformDefault()
    {
        await using var os = TestOS.Local().RegisterCatalog();
        var settings = await Settings(os, "models");

        var expectedDefault = ClientComputeConsentPolicy.DefaultAllowClientCodeSandboxForCurrentPlatform();
        Assert.Equal(expectedDefault, settings.App.State.AllowClientCodeSandbox);

        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));
        var view = await settings.Screen.Read();
        var toggleSlot = Slots(view!.View).Single(node =>
            node.GetProperty("neuronUri").GetString()!.EndsWith("/" + ClientComputeConsentPolicy.SettingsToggleKey, StringComparison.Ordinal));

        Assert.Equal(expectedDefault, toggleSlot.GetProperty("props").GetProperty("isOn").GetBoolean());
        Assert.Contains(ICodeSandbox.CapabilityId, view.View.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TogglingAllowClientCodeSandboxPropagatesToConsentStoreUserSettingsAndDeviceLink()
    {
        RecordingDeviceLink.Reset();
        var consentStore = new TestClientCodeSandboxConsent(ClientComputeConsentPolicy.DefaultAllowClientCodeSandboxForCurrentPlatform());
        var services = new ServiceCollection();
        services.AddSingleton<IClientCodeSandboxConsent>(consentStore);
        await using var provider = services.BuildServiceProvider();

        try
        {
            await using var os = TestOS.Local(o => o.Services = provider)
                .RegisterCatalog()
                .Register<RecordingDeviceLink>();
            var settings = await Settings(os, "models");

            await Send(settings, ClientComputeConsentPolicy.SettingsToggleKey, new SetChecked(false));

            Assert.False(settings.App.State.AllowClientCodeSandbox);
            Assert.False(consentStore.AllowClientCodeSandbox);
            Assert.Equal([false], RecordingDeviceLink.Calls);
            await settings.Request("reload-preferences", new SettingsReloadPreferences());
            Assert.False(settings.App.State.AllowClientCodeSandbox);
            Assert.NotNull(settings.App.State.Variables);
            Assert.Equal("false", settings.App.State.Variables!.Single(v => v.Key == ClientComputeConsentPolicy.SettingsVariableKey).Value);

            await Send(settings, ClientComputeConsentPolicy.SettingsToggleKey, new SetChecked(true));

            Assert.True(settings.App.State.AllowClientCodeSandbox);
            Assert.True(consentStore.AllowClientCodeSandbox);
            Assert.Equal([false, true], RecordingDeviceLink.Calls);
            await settings.Request("reload-preferences", new SettingsReloadPreferences());
            Assert.True(settings.App.State.AllowClientCodeSandbox);
            Assert.NotNull(settings.App.State.Variables);
            Assert.Equal("true", settings.App.State.Variables!.Single(v => v.Key == ClientComputeConsentPolicy.SettingsVariableKey).Value);
        }
        finally
        {
            RecordingDeviceLink.Reset();
        }
    }

    [Fact]
    public void ApplyUserSettingsHydratesAllowClientCodeSandboxFromPersistedToggleVariable()
    {
        var baseline = new SettingsAppState();
        var disabled = baseline.ApplyUserSettings(
            new UserSettingsState
            {
                Variables = [new UserVariableEntry(ClientComputeConsentPolicy.SettingsVariableKey, "false")],
            },
            "Loaded");
        Assert.False(disabled.AllowClientCodeSandbox);

        var enabled = disabled.ApplyUserSettings(
            new UserSettingsState
            {
                Variables = [new UserVariableEntry(ClientComputeConsentPolicy.SettingsVariableKey, "true")],
            },
            "Loaded");
        Assert.True(enabled.AllowClientCodeSandbox);
    }

    private sealed class TestClientCodeSandboxConsent(bool initial) : IClientCodeSandboxConsent
    {
        private bool _allow = initial;
        public bool AllowClientCodeSandbox => _allow;
        public bool HasExplicitOverride { get; private set; }
        public event Action<bool>? Changed;
        public bool Resolve(bool fallbackDefault) => HasExplicitOverride ? _allow : fallbackDefault;
        public void Set(bool allow)
        {
            _allow = allow;
            HasExplicitOverride = true;
            Changed?.Invoke(allow);
        }
    }

    [Neuron("devices.link"), StatelessNeuron]
    private sealed class RecordingDeviceLink : Neuron, IDeviceLink
    {
        public static List<bool> Calls { get; } = [];

        public static void Reset() => Calls.Clear();

        public Task<LinkStatus> Join(Uri server, Code code)
            => Task.FromResult(new LinkStatus(LinkState.Connected, server, "test-pc", null, AllowClientCodeSandbox: true));

        public Task<LinkStatus> Status()
            => Task.FromResult(new LinkStatus(LinkState.Connected, new Uri("https://cloud.test"), "test-pc", null, AllowClientCodeSandbox: true));

        public Task<LinkStatus> SetAllowClientCodeSandbox(bool allow)
        {
            Calls.Add(allow);
            return Task.FromResult(new LinkStatus(
                LinkState.Connected,
                new Uri("https://cloud.test"),
                "test-pc",
                null,
                AllowClientCodeSandbox: allow,
                AdvertisedCapabilities: ClientComputeConsentPolicy.CapabilitiesFor(allow)));
        }

        public Task Leave() => Task.CompletedTask;
    }

    [Neuron("ai.models"), StatelessNeuron]
    private sealed class RecordingSettingsModelCatalog : Neuron, IModelCatalog
    {
        public static (ModelRole Role, ModelId Model)? LastSetRole { get; private set; }

        public static void Reset() => LastSetRole = null;

        public Task<ModelCard[]> List(Abilities need = Abilities.None)
            => Task.FromResult(KnownModels.All.Where(c => need == Abilities.None || (c.Can & need) == need).ToArray());

        public Task<ModelId> Pick(ModelRole role, ModelId? turn = null)
            => Task.FromResult(turn ?? KnownModels.Defaults.Device.GetValueOrDefault(role, KnownModels.Qwen35.Id));

        public Task SetRole(ModelRole role, ModelId model)
        {
            LastSetRole = (role, model);
            return Task.CompletedTask;
        }

        public Task<RoleMap> Roles() => Task.FromResult(KnownModels.Defaults);

        public Task<ModelAvailability[]> Availability()
            => Task.FromResult(KnownModels.All.Select(c => new ModelAvailability(c.Id, true, null)).ToArray());

        public Task SaveProviderCredential(string provider, string credentialValue) => Task.CompletedTask;
    }
}
