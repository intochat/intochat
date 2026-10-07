namespace DigitalBrain.Apps;

// The apps the image ships: code, not content anyone wrote at runtime. Loaded once on first read,
// read-only afterwards. A bundle that does not compile never reaches the catalog, so loading is
// where shipped apps pass the publish gate.
public sealed class ShippedCatalog
{
    private readonly Lazy<Task<IReadOnlyDictionary<(string Publisher, string Name), AppBundle>>> _bundles;

    public ShippedCatalog(IEnumerable<IShippedAppSource> sources, IScriptSandbox? sandbox = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var snapshot = sources.ToArray();
        _bundles = new(() => Load(snapshot, sandbox));
    }

    public async Task<AppBundle?> Find(string publisher, string name)
        => (await _bundles.Value).GetValueOrDefault((publisher, name));

    public async Task<IReadOnlyList<MarketplaceListing>> Listings()
        => [.. (await _bundles.Value).Select(entry => new MarketplaceListing(
            entry.Key.Publisher, new AppSource.Shipped(), entry.Key.Name,
            entry.Value.Manifest.Title, entry.Value.Manifest.Description, entry.Value.Version, entry.Value.Manifest.Icon))];

    private static async Task<IReadOnlyDictionary<(string Publisher, string Name), AppBundle>> Load(
        IShippedAppSource[] sources, IScriptSandbox? sandbox)
    {
        var bundles = new SortedDictionary<(string Publisher, string Name), AppBundle>();
        foreach (var source in sources)
        {
            foreach (var bundle in source.Load())
            {
                if (!bundles.TryAdd((source.Publisher, bundle.Name), bundle))
                { throw new InvalidOperationException($"{source.Publisher}/{bundle.Name} ships twice."); }
                await RequireCompiles(source.Publisher, bundle, sandbox);
            }
        }
        return bundles;
    }

    // The same gate AppNeuron.Publish applies; a host without a sandbox cannot check and does not.
    private static async Task RequireCompiles(string publisher, AppBundle bundle, IScriptSandbox? sandbox)
    {
        var scripts = bundle.Files.Where(file => file.Key.EndsWith(".cs", StringComparison.Ordinal))
            .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
        if (scripts.Count == 0 || sandbox is null) { return; }
        ScriptCompilationCheck check;
        try { check = await sandbox.Compile(scripts, CancellationToken.None); }
        catch (InvalidOperationException) { return; }
        if (check.Success) { return; }
        throw new InvalidOperationException($"{publisher}/{bundle.Name} {bundle.Version} does not compile: "
            + string.Join("; ", check.Errors.Select(error => $"{error.File}:{error.Line} {error.Message}")));
    }
}
