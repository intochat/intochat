using DigitalBrain.Apps;

namespace DigitalBrain.Modules.Apps.Tests.Integration;

// An app.cs for a shipped folder fixture, written the way the system writes one.
internal static class Declarations
{
    public static string Of(string name, string version, string title, string description,
        AppOperation[]? operations = null, AppSetting[]? settings = null, string? icon = null, string spec = "")
        => AppDeclaration.Write(new(name, version, new(title, description, operations ?? [], settings ?? [], [], icon), spec));
}
