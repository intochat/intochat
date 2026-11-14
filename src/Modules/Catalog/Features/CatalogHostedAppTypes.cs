using IntoChat.OS.Framework;

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

        string name;
        var appsMarker = appKey.IndexOf("/apps/", StringComparison.Ordinal);
        if (appsMarker > 0)
        {
            if (appKey.AsSpan(0, appsMarker).Contains('/'))
            {
                return false;
            }
            name = appKey[(appsMarker + "/apps/".Length)..];
            if (name.Length == 0 || name.Contains('/'))
            {
                return false;
            }
        }
        else if (!appKey.Contains('/'))
        {
            name = appKey;
        }
        else
        {
            return false;
        }

        return catalog.IsCompiledHostApp(name);
    }
}
