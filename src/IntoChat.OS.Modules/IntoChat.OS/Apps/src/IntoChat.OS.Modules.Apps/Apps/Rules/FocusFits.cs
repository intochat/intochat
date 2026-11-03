using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record FocusFits(string[] Keys) : Rule("apps.focus")
{
    public override bool Broken => Keys.Length > AppNeuron.MaxFocus || Keys.Any(k => k.Length > AppNeuron.MaxFocusKeyLength);
    public override string Message => Keys.Length > AppNeuron.MaxFocus
        ? $"Focus on at most {AppNeuron.MaxFocus} neurons."
        : $"A focus key is at most {AppNeuron.MaxFocusKeyLength} characters.";
}
