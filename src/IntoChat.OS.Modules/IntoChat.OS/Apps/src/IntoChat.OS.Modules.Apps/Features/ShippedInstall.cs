namespace IntoChat.OS.Modules.Apps;

// Installs the boot set: every app in the shipped catalog, plus any ShippedApps root the catalog does not
// carry. A present app is left as it is.
public static class ShippedInstall
{
    public static async Task Missing(FeatureCatalog catalog, FeatureLoader loader, string brainId, Func<string, Task<AppSnapshot>> read,
        Action<Exception, string>? failed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(read);
        foreach (var (publisher, name) in await catalog.BootSet())
        {
            try
            {
                if ((await read(name).WaitAsync(ct)).Manifest.Title.Length > 0) { continue; }
                var bundle = await catalog.Find(publisher, name)
                    ?? throw new InvalidOperationException($"{publisher}/{name} is not in the catalog.");
                await loader.Load(brainId, name, bundle, new AppOrigin(publisher, new AppSource.Shipped(), name, bundle.Version), ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { failed?.Invoke(error, $"{publisher}/{name}"); }
        }
    }
}
