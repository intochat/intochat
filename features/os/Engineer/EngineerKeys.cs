using IntoChat.OS.Modules.AI.Models;

public static partial class Engineer
{
    // The brain's own model catalog, so a model the brain unlocked with its own key is a valid choice here.
    public static string ModelCatalogKey(string scope) => IModelCatalog.Key(scope);
}
