using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record BuilderIsIdle(AppStatus Status, DateTimeOffset? BuildStartedAt, TimeSpan FreshFor, DateTimeOffset Now) : Rule("apps.builder-idle")
{
    public override bool Broken => Status == AppStatus.Building && BuildStartedAt is { } started && Now - started < FreshFor;
    public override string Message => "The Builder is already working on this app.";
}
