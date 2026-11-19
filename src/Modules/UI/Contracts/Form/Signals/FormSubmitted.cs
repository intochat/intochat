using DigitalBrain;

namespace IntoChat.OS.Modules.UI.Form.Signals;

[Signal("ui.form-submitted")]
public sealed record FormSubmitted(
    string FormId,
    int Version,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, SecretRef> SecretRefs) : Signal;
