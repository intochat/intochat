using System.Text.Json;
using System.Text.Json.Nodes;
using IntoChat.OS.Modules.AI.Agents.Context;

namespace IntoChat.OS.Modules.Apps;

// Writes who the user is (name, language, time zone, date format) and their default model into the user.profile context,
// so the Ino agent answers in their terms. The synapse on the settings neuron is made by Ensure; Ino calls it when it opens.
[DigitalBrain.Neurons.Neuron("ino.profile-writer")]
public sealed class ProfileContextWriter : DigitalBrain.Neurons.Neuron, IProfileContextWriter
{
    private DigitalBrain.NeuronId SettingsId => new(Id.Brain, "os.settings", "settings");

    private IAgentContext Profile => Get<IAgentContext>(new DigitalBrain.NeuronId(Id.Brain, "agent-context", "context/user.profile"));

    private IAgentContext ConversationInference => Get<IAgentContext>(new DigitalBrain.NeuronId(Id.Brain, "agent-context", "context/conversation.inference"));

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
        if (string.IsNullOrWhiteSpace(settings.DefaultModel))
        {
            await Profile.Remove("inference");
            await SyncConversationInferenceModel(null);
            return;
        }
        var model = settings.DefaultModel.Trim();
        var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["model"] = model });
        await Profile.Set(new ContextEntry("inference", json, [], [ContextTags.Inference]));
        await SyncConversationInferenceModel(model);
    }

    private async Task SyncConversationInferenceModel(string? model)
    {
        var context = ConversationInference;
        var snapshot = await context.Read();
        if (snapshot.Entries.FirstOrDefault(entry => entry.Name == "inference") is not { } existing) { return; }
        JsonObject? inference;
        try { inference = JsonNode.Parse(existing.Text) as JsonObject; }
        catch (JsonException) { return; }
        if (inference is null || !inference.ContainsKey("model")) { return; }
        if (model is null)
        {
            inference.Remove("model");
            if (inference.Count == 0)
            {
                await context.Remove("inference");
                return;
            }
        }
        else
        {
            if (string.Equals(inference["model"]?.GetValue<string>(), model, StringComparison.Ordinal)) { return; }
            inference["model"] = model;
        }
        await context.Set(new ContextEntry("inference", inference.ToJsonString(), [], [ContextTags.Inference]));
    }
}
