using System.Reflection;

namespace IntoChat.OS.Modules.Apps;

// Registers an assembly whose compiled [App] types ship in FeatureCatalog. Boot names the app types the host assigns to every brain
// at start (ino and settings are the shell's); the host finds their bundles here, so it names no app.
public sealed record CompiledAppsRegistration(Assembly Assembly, Type[]? Boot = null);
