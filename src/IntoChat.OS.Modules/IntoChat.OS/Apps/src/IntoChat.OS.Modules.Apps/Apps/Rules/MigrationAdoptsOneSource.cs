using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record MigrationAdoptsOneSource(string? Adopted, string Source) : Rule("apps.migration-source")
{
    public override bool Broken => Adopted is not null && Adopted != Source;
    public override string Message => "This migration already adopted another source.";
}
