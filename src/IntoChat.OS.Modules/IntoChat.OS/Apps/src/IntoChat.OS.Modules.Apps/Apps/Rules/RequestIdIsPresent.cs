using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record RequestIdIsPresent(Guid Id) : Rule("apps.request-id")
{
    public override bool Broken => Id == Guid.Empty;
    public override string Message => "A request identity is required.";
}
