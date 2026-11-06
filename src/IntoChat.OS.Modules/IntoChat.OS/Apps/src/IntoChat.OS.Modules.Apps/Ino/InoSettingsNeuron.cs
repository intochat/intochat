using System.Text.Json;
using System.Text.Json.Nodes;
using IntoChat.OS.Modules.AI.Agents.Context;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Neuron("ino.settings")]
public sealed class InoSettingsNeuron : DigitalBrain.Neuron, IInoSettings
{
    private IUserSettings Settings => Get<IUserSettings>(new DigitalBrain.NeuronId(Id.Brain, "os.settings", "settings"));

    private IAgentContext InferenceContext => Get<IAgentContext>(new DigitalBrain.NeuronId(Id.Brain, "agent-context", "context/conversation.inference"));

    public async Task<InoToolResult> Navigate(string section, string? item = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        var route = string.IsNullOrWhiteSpace(item) ? section : $"{section}:{item}";
        var label = string.IsNullOrWhiteSpace(item) ? section : $"{section} / {item}";
        try
        {
            await Get<IApp>(new DigitalBrain.NeuronId(Id.Brain, IntoChat.OS.Connections.AppGrainType.Name, "apps/settings")).Request("navigate", route);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The Settings app is optional; the card still tells the user where to look.
            _ = error.Message;
        }

        var card = new InoCard(
            $"settings-{route}",
            "settings",
            $"Settings · {label}",
            $"Autonomous Settings co-pilot navigated to {route}.",
            "neuron://local/apps/settings",
            "ready",
            $"neuron://local/apps/ino#settings-{route}");
        return new InoToolResult($"Navigated Settings to {label}.", card);
    }

    public async Task<InoToolResult> SetModel(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var model = modelId.Trim();
        var current = await Settings.Read();
        await Settings.SetProfile(current.DisplayName, current.Theme, model);
        await WriteInference(inference => inference["model"] = model);
        return new InoToolResult($"Switched model to {model}.");
    }

    public async Task<InoToolResult> SetTemperature(float temperature)
    {
        if (float.IsNaN(temperature) || temperature < 0f || temperature > 2f)
        {
            throw new ArgumentOutOfRangeException(nameof(temperature), "Temperature is between 0 and 2.");
        }

        await WriteInference(inference => inference["temperature"] = temperature);
        return new InoToolResult($"Set temperature to {temperature}.");
    }

    public async Task<InoToolResult> SetVariable(string key, string value)
    {
        await Settings.SetVariable(key, value);
        return new InoToolResult($"Set variable {key}.");
    }

    // One entry carries every inference setting, so a write starts from what is already there.
    private async Task WriteInference(Action<JsonObject> change)
    {
        var context = InferenceContext;
        var snapshot = await context.Read();
        var existing = snapshot.Entries.FirstOrDefault(entry => entry.Name == "inference");
        JsonObject inference;
        try { inference = existing is null ? new JsonObject() : JsonNode.Parse(existing.Text) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { inference = new JsonObject(); }
        change(inference);
        await context.Set(new ContextEntry("inference", inference.ToJsonString(), [], [ContextTags.Inference]));
    }
}
