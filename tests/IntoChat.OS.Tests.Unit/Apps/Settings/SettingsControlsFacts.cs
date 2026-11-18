using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Modules.Apps;

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
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var settings = await Settings(os, "desktop");
        Assert.True(settings.App.State.ShowOsBarClock);

        await Send(settings, "bar-clock", new Toggle());

        Assert.False(settings.App.State.ShowOsBarClock);
        await Send(settings, "bar-clock", new Toggle());
        Assert.True(settings.App.State.ShowOsBarClock);
    }

    [Fact]
    public async Task The_switch_shows_the_preference_before_anyone_touches_it()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        await Settings(os, "notifications");

        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));
        var view = await os.Get<IScreen>(Control("control.toggleSwitch", "notify-approvals")).Read();

        Assert.True(view!.View.GetProperty("root").GetProperty("isOn").GetBoolean());
    }

    [Fact]
    public async Task Choosing_a_bar_layout_through_its_ComboBox_slot_saves_it()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var settings = await Settings(os, "desktop");

        await Send(settings, "bar-layout", new DigitalBrain.UI.Commands.Select("compact-pill"));

        Assert.Equal("compact-pill", settings.App.State.BarLayout);
    }

    [Fact]
    public async Task Submitting_the_settings_form_stores_values()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
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
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var settings = await Settings(os, "general");

        await Send(settings, "search", new Submit(new Dictionary<string, string> { ["query"] = "Ollama" }));

        Assert.Equal("Ollama", settings.App.State.SearchQuery);
    }

    [Fact]
    public async Task A_managed_setting_is_drawn_disabled_and_refuses()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var settings = await os.Install<SettingsApp>();
        await settings.Request("set-variable", new SettingsSetVariable(SettingsControls.ManagedVariable, "quiet-hours"));
        await settings.Request("navigate", new SettingsNavigate("notifications"));

        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => Send(settings, "quiet-hours", new Toggle()));

        Assert.Equal(ControlProps.Disabled, refused.Reason);
        Assert.False(settings.App.State.QuietHoursEnabled);
    }

    [Fact]
    public async Task A_form_value_over_its_limit_is_refused()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var settings = await Settings(os, "general");

        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(
            () => Send(settings, "regional", new Submit(new Dictionary<string, string> { ["displayName"] = new string('a', 65) })));

        Assert.Equal(FormRefusals.TooLong, refused.Reason);
        Assert.Equal("User", settings.App.State.DisplayName);
    }
}
