using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record AliasDoesNotRun(bool IsAlias) : Rule("apps.alias-runs-nothing")
{
    public override bool Broken => IsAlias;
    public override string Message => "An alias cannot run its retired program.";
}
