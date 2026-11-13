using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

[NeuronAlias("ino.settings"), DefaultNeuronType("ino.settings")]
public interface IInoSettings
{
    Task<InoToolResult> Navigate(string section, string? item = null);

    // Also writes the conversation.inference context entry so the running agent switches model.
    Task<InoToolResult> SetModel(string modelId);

    // Same inference entry as SetModel; each keeps the other's field.
    Task<InoToolResult> SetTemperature(float temperature);

    Task<InoToolResult> SetVariable(string key, string value);
}
