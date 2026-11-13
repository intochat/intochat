using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record BlackboardKeysAreDeclarable(string[] Keys) : Rule("apps.presentation-keys")
{
    private static readonly string[] ReservedBlackboardKeys = ["running", "attention"];

    public override bool Broken => Keys.Any(key => string.IsNullOrWhiteSpace(key)
        || ReservedBlackboardKeys.Contains(key, StringComparer.Ordinal));
    public override string Message => "Declare nonempty keys; running and attention are reserved blackboard keys.";
}
