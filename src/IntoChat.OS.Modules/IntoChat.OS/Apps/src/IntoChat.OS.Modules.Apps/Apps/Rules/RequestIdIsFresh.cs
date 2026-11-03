using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record RequestIdIsFresh(bool Reused) : Rule("apps.request-id-fresh")
{
    public override bool Broken => Reused;
    public override string Message => "A request identity cannot be reused for another command.";
}
