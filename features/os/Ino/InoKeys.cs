using IntoChat.OS.AI.Models;
using IntoChat.OS.Client;

public static partial class Ino
{
    public static string Key(IntoChatOSConnection brain) => brain.Setting("App")!;
    public static string DataKey(IntoChatOSConnection brain) => DataKey(brain.Setting("ChatDataApp"), Key(brain));
    public static string DataKey(string? chatDataApp, string app) => chatDataApp is { Length: > 0 } ? chatDataApp : app;
    public static string Scope(IntoChatOSConnection brain) => brain.Setting("Brain")!;

    // The brain's own model catalog, so a model the brain unlocked with its own key is a valid choice here.
    public static string ModelCatalogKey(string scope) => IModelCatalog.Key(scope);
    public static IModelCatalog Models(IntoChatOSConnection brain) => brain.Get<IModelCatalog>(ModelCatalogKey(Scope(brain)));
}
