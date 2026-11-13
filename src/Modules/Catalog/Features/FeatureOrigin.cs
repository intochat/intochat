namespace IntoChat.OS.Modules.Apps;

// Where a catalog entry was read from: a compiled [App] whose bundle ships in a module assembly.
public abstract record FeatureOrigin
{
    public sealed record Module(System.Reflection.Assembly Assembly) : FeatureOrigin;
}
