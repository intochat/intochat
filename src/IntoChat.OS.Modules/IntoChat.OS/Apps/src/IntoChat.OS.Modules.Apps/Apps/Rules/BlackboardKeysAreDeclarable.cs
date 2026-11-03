using DigitalBrain.Rules;
using IntoChat.OS.Modules.Flame;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record BlackboardKeysAreDeclarable(string[] Keys) : Rule("apps.presentation-keys")
{
    public override bool Broken => Keys.Any(key => string.IsNullOrWhiteSpace(key)
        || FlameSchema.ReservedBlackboardKeys.Contains(key, StringComparer.Ordinal));
    public override string Message => "Declare nonempty keys; running and attention are reserved blackboard keys.";
}
