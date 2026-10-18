using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Kernel;

// Written by the Workshop when the brain adds a module. Empty in a fresh checkout. Keep the shape:
// one `new XModule()` per added module, so OsCatalog composes them with the built-ins.
public static partial class OsCatalog
{
    private static partial Module[] Generated() => [];
}
