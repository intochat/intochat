using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record MigrationIsRecorded(bool Recorded) : Rule("apps.migration-recorded")
{
    public override bool Broken => !Recorded;
    public override string Message => "Request adoption requires a migration.";
}
