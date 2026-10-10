namespace DigitalBrain.Apps;

// The apps the image ships: code, not content anyone wrote at runtime. Loaded once on first read;
// only Save to source (a development folder written back) reloads it. A bundle that does not
// compile never reaches the catalog, so loading is where shipped apps pass the publish gate.
public sealed class ShippedCatalog
{
    private readonly IShippedAppSource[] _sources;
    private readonly IScriptSandbox? _sandbox;
    private readonly Lock _gate = new();
    private Task<IReadOnlyDictionary<(string Publisher, string Name), AppBundle>>? _bundles;

    public ShippedCatalog(IEnumerable<IShippedAppSource> sources, IScriptSandbox? sandbox = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToArray();
        _sandbox = sandbox;
    }

    public async Task<AppBundle?> Find(string publisher, string name)
        => (await Bundles()).GetValueOrDefault((publisher, name));

    public async Task<IReadOnlyList<MarketplaceListing>> Listings()
        => [.. (await Bundles()).Select(entry => new MarketplaceListing(
            entry.Key.Publisher, new AppSource.Shipped(), entry.Key.Name,
            entry.Value.Manifest.Title, entry.Value.Manifest.Description, entry.Value.Version, entry.Value.Manifest.Icon))];

    // Writes the bundle into the publisher's development folder and reloads, so Find and Listings
    // switch to the new contents together. A failed reload leaves the previous snapshot in place.
    public async Task<AppBundle> SaveToSource(string publisher, AppBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var source = _sources.OfType<DirectoryShippedAppSource>().FirstOrDefault(candidate => candidate.Publisher == publisher && candidate.Development)
            ?? throw new InvalidOperationException($"{publisher} has no development source folder to save {bundle.Name} into.");
        source.Write(bundle);
        var reloaded = Load(_sources, _sandbox);
        var bundles = await reloaded;
        lock (_gate) { _bundles = reloaded; }
        return bundles.GetValueOrDefault((publisher, bundle.Name))
            ?? throw new InvalidOperationException($"{publisher}/{bundle.Name} was written but did not load back.");
    }

    private Task<IReadOnlyDictionary<(string Publisher, string Name), AppBundle>> Bundles()
    {
        lock (_gate) { return _bundles ??= Load(_sources, _sandbox); }
    }

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
        scripts[AppFiles.DeclarationPath] = AppDeclaration.Write(new(bundle.Name, bundle.Version, bundle.Manifest, bundle.Spec));
        ScriptCompilationCheck check;
        try { check = await sandbox.Compile(scripts, CancellationToken.None); }
        catch (InvalidOperationException) { return; }
        if (check.Success) { return; }
        throw new InvalidOperationException($"{publisher}/{bundle.Name} {bundle.Version} does not compile: "
            + string.Join("; ", check.Errors.Select(error => $"{error.File}:{error.Line} {error.Message}")));
    }
}
