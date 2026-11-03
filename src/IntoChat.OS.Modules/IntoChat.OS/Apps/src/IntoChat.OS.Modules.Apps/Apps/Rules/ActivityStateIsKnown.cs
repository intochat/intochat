using DigitalBrain.Rules;
using IntoChat.OS.Modules.Apps.Signals;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record ActivityStateIsKnown(string State) : Rule("apps.activity-state")
{
    public override bool Broken => !AppActivity.States.Contains(State, StringComparer.Ordinal);
    public override string Message => $"'{State}' is not an activity state: {string.Join(", ", AppActivity.States)}.";
}
