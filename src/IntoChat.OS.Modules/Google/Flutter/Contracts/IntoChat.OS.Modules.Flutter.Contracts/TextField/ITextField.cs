using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.TextField;

[NeuronAlias("textfield"), DefaultNeuronType(UIVocabulary.TextFieldType)]
public interface ITextField
{
    Task Configure(string label, string kind, string? submitButton = null);
    Task SetValue(string value);
    Task<bool> TrySetValue(string value, int expectedVersion);
    Task Input(string value);
    [DigitalBrain.ReadOnlyCall] Task<TextFieldState> Read();
}

[GenerateSerializer, Alias("ui.textfield-state")]
[ExportableState]
public sealed class TextFieldState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public string Label { get; set; } = "";
    [Id(3)] public string Kind { get; set; } = "text";
    [Id(4)] public string Value { get; set; } = "";
    [Id(5)] public string? SubmitButton { get; set; }
}
