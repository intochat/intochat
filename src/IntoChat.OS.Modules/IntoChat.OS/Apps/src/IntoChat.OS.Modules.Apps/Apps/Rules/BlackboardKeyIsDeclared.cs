using DigitalBrain.Rules;
using IntoChat.OS.Modules.Flame;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record BlackboardKeyIsDeclared(string Key, string[] Declared) : Rule("apps.blackboard-key")
{
    public override bool Broken => FlameSchema.ReservedBlackboardKeys.Contains(Key, StringComparer.Ordinal)
        || !Declared.Contains(Key, StringComparer.Ordinal);
    public override string Message => $"'{Key}' is not a script-writable declared blackboard key.";
}
