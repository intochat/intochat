using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record ProgramPathIsWritable(string Path) : Rule("apps.program-path")
{
    public override bool Broken => Path == AppFiles.DeclarationPath || !AppFiles.IsProgram(Path);
    public override string Message => Path == AppFiles.DeclarationPath
        ? $"{AppFiles.DeclarationPath} is the app's declaration: change it through SaveManifest and SaveSpec."
        : "A program file is a .cs file: a synapse (top-level statements) or a shared class.";
}
