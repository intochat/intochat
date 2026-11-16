using IntoChat.OS.Modules.AI.Agents.Context;

namespace IntoChat.OS.Modules.Apps;

// Writes who the user is (name, language, time zone, date format) into the user.profile context,
// so the Ino agent answers in their terms. Model selection is decided per turn by ModelCatalog.Decide
// and never overwritten in user.profile or conversation.inference here.
[DigitalBrain.Neurons.Neuron("ino.profile-writer")]
internal sealed class ProfileContextWriter : DigitalBrain.Neurons.Neuron, IProfileContextWriter
{
    private DigitalBrain.NeuronId SettingsId => new(Id.Brain, "os.settings", "settings");

    private IAgentContext Profile => Get<IAgentContext>(new DigitalBrain.NeuronId(Id.Brain, "agent-context", "context/user.profile"));

    public async Task Ensure()
    {
        await Context.Synapses.Ensure<UserSettingsChanged>(SettingsId);
        await Write(await Get<IUserSettings>(SettingsId).Read());
    }

    [On]
    public Task OnSettingsChanged(UserSettingsChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        return Write(new UserSettingsState
        {
            DisplayName = changed.DisplayName,
            Theme = changed.Theme,
            Language = changed.Language,
            TimeZone = changed.TimeZone,
            DateFormat = changed.DateFormat,
        });
    }

    private Task Write(UserSettingsState settings)
    {
        var text = $"Name: {settings.DisplayName}\nLanguage: {settings.Language}\nTime zone: {settings.TimeZone}\nDate format: {settings.DateFormat}";
        return Profile.Set(new ContextEntry("profile", text, []));
    }
}
