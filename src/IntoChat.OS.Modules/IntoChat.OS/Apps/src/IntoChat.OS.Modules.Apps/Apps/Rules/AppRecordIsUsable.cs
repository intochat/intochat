using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record AppRecordIsUsable(string AppName, string? Refused) : Rule("apps.record-refused")
{
    public override bool Broken => Refused is not null;
    public override string Message => $"App {AppName} was refused on activation ({Refused}); the Creator must rebuild it before it can be changed here.";
}
