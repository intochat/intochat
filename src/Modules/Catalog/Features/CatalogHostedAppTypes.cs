using IntoChat.OS.Framework;
using IntoChat.OS.Host.Kernel;

namespace IntoChat.OS.Modules.Apps;

// Answers whether the host itself loaded an app's compiled type from a registered module assembly (AppTrust step 2).
// Edge UI dispatch passes "{scope}/apps/{name}"; AppNeuron legacy cleansing passes bare "{name}".
internal sealed class CatalogHostedAppTypes(FeatureCatalog catalog) : IHostedAppTypes
{
    public bool IsHostLoaded(string appKey)
    {
        if (string.IsNullOrWhiteSpace(appKey))
        {
            return false;
        }

        var name = IntoChat.OS.Host.Primitives.AppRef.TryParse(appKey, out var app)
            ? app.Name
            : !appKey.Contains('/') ? appKey : null;
        return name is not null && catalog.IsCompiledHostApp(name);
    }
}
