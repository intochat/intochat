namespace IntoChat.OS.Modules.Apps;

// Where a catalog entry was read from. A script is a folder under the script root. A compiled app
// is an [App] whose bundle ships in the module assembly.
public abstract record FeatureOrigin
{
    public sealed record Folder(string Path) : FeatureOrigin;

    public sealed record Module(System.Reflection.Assembly Assembly) : FeatureOrigin;
}
