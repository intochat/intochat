using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record RequestPayloadFits(int Length) : Rule("apps.request-payload")
{
    public override bool Broken => Length > 64 * 1024;
    public override string Message => "A request input is at most 64 KiB.";
}
