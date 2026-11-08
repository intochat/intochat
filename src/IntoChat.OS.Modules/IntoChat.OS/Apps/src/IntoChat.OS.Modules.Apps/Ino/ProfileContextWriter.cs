using System.Text.Json;
using IntoChat.OS.Modules.AI.Agents.Context;

namespace IntoChat.OS.Modules.Apps;

// Writes who the user is (name, language, time zone, date format) and their default model into the user.profile context,
// so the Ino agent answers in their terms. The synapse on the settings neuron is made by Ensure; Ino calls it when it opens.
[DigitalBrain.Neuron("ino.profile-writer")]
public sealed class ProfileContextWriter : DigitalBrain.Neuron, IProfileContextWriter
{
    private DigitalBrain.NeuronId SettingsId => new(Id.Brain, "os.settings", "settings");

    private IAgentContext Profile => Get<IAgentContext>(new DigitalBrain.NeuronId(Id.Brain, "agent-context", "context/user.profile"));

    public async Task Ensure()
    {
        await Context.Synapses.Ensure<UserSettingsChanged>(SettingsId);
        await Write(await Get<IUserSettings>(SettingsId).Read());
    }

    [DigitalBrain.On]
    public Task OnSettingsChanged(UserSettingsChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        return Write(new UserSettingsState
        {
            DisplayName = changed.DisplayName,
            Theme = changed.Theme,
            DefaultModel = changed.DefaultModel,
            Language = changed.Language,
            TimeZone = changed.TimeZone,
            DateFormat = changed.DateFormat,
        });
    }

    private async Task Write(UserSettingsState settings)
    {
        var text = $"Name: {settings.DisplayName}\nLanguage: {settings.Language}\nTime zone: {settings.TimeZone}\nDate format: {settings.DateFormat}";
        await Profile.Set(new ContextEntry("profile", text, []));
        if (string.IsNullOrWhiteSpace(settings.DefaultModel)) { await Profile.Remove("inference"); return; }
        var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["model"] = settings.DefaultModel });
        await Profile.Set(new ContextEntry("inference", json, [], [ContextTags.Inference]));
    }
}
