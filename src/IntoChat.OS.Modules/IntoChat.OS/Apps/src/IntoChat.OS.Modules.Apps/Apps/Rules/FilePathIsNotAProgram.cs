using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record FilePathIsNotAProgram(string Path) : Rule("apps.file-path")
{
    public override bool Broken => Path.EndsWith(".cs", StringComparison.Ordinal);
    public override string Message => "Programs are written through WriteBehavior; app.cs through SaveManifest and SaveSpec.";
}
