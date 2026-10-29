using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MicrosoftSdk = Microsoft;

namespace IntoChat.OS.Modules.Apps;

// The features the host loads from its script folder (IntoChatOS:Modules:apps:Options:Features):
// code, not content anyone wrote at runtime. Loaded at boot and on first read; only Save to source
// reloads it. A bundle that does not compile never reaches the catalog, so loading is where a
// feature passes the publish gate; one that fails is skipped and reported through Failures, and
// every other feature still loads. Compiled [App] types in this module are listed beside the scripts.
// Unset, the folder is features/os (the repository in Development, the publish directory otherwise).
public sealed class FeatureCatalog
{
    internal static readonly ActivitySource Telemetry = new("IntoChat.OS.Modules.Apps.Features");

    private readonly FeatureDirectory? _directory;
    private readonly IScriptSandbox? _sandbox;
    private readonly Assembly? _modules;
    private readonly FeatureDirectory? _dev;
    private readonly SideloadDirectory? _sideload;
    private readonly GitSideload? _git;
    private readonly Lock _gate = new();
    private Task<Loaded>? _loaded;

    public FeatureCatalog(FeatureDirectory? directory, IScriptSandbox? sandbox = null, FeatureDirectory? dev = null, SideloadDirectory? sideload = null, GitSideload? git = null)
        : this(directory, sandbox, null, dev, sideload, git) { }

    internal FeatureCatalog(FeatureDirectory? directory, IScriptSandbox? sandbox, Assembly? modules, FeatureDirectory? dev = null, SideloadDirectory? sideload = null, GitSideload? git = null)
    {
        _directory = directory;
        _sandbox = sandbox;
        _modules = modules;
        _dev = dev;
        _sideload = sideload;
        _git = git;
    }

    // A relative folder resolves against the host's base directory, where a published image carries it.
    // Unset walks to features/os: the repo root in Development, <base>/features/os when published.
    internal static FeatureCatalog From(IServiceProvider services)
    {
        var path = services.GetRequiredService<IOptions<AppsOptions>>().Value.Features;
        var development = services.GetService<IHostEnvironment>()?.IsDevelopment() == true;
        if (string.IsNullOrWhiteSpace(path)) { path = DefaultScriptFolder(development); }
        var directory = new FeatureDirectory(System.IO.Path.GetFullPath(path, AppContext.BaseDirectory), development);
        return new(directory, services.GetService<IScriptSandbox>(), typeof(AppsModule).Assembly, DevDirectory(services), SideloadRoot(services), services.GetService<GitSideload>());
    }

    // IntoChatOS:Apps:SideloadDirectory, or <Home>/apps. Missing until a folder or a git URL is dropped.
    private static SideloadDirectory? SideloadRoot(IServiceProvider services)
    {
        try
        {
            var configuration = services.GetService<MicrosoftSdk.Extensions.Configuration.IConfiguration>();
            var configured = configuration?["IntoChatOS:Apps:SideloadDirectory"];
            if (!string.IsNullOrWhiteSpace(configured)) { return new SideloadDirectory(System.IO.Path.GetFullPath(configured)); }
            var home = DigitalBrain.Runtime.Orleans.StateHome.Resolve(configuration);
            return new SideloadDirectory(home.Apps);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        { return null; }
    }

    // <Home>/dev-apps, owner folders underneath. Missing until the first `intochat run`.
    private static FeatureDirectory? DevDirectory(IServiceProvider services)
    {
        try
        {
            var configuration = services.GetService<MicrosoftSdk.Extensions.Configuration.IConfiguration>();
            var home = DigitalBrain.Runtime.Orleans.StateHome.Resolve(configuration);
            return new FeatureDirectory(System.IO.Path.Combine(home.Path, "dev-apps"), writable: true);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        { return null; }
    }

    // Development: the features/os next to Intochat.OS.slnx, walking up from the build output.
    // Published: the features/os copied beside the host.
    internal static string DefaultScriptFolder(bool development)
    {
        if (development)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(System.IO.Path.Combine(dir.FullName, "Intochat.OS.slnx")))
                { return System.IO.Path.Combine(dir.FullName, "features", "os"); }
            }
        }
        return System.IO.Path.Combine(AppContext.BaseDirectory, "features", "os");
    }

    public FeatureDirectory? Directory => _directory;

    public async Task<IReadOnlyList<FeatureFailure>> Failures() => (await Snapshot()).Failures;

    public async Task<AppBundle?> Find(string owner, string name)
        => (await Bundles()).GetValueOrDefault((owner, name));

    public async Task<bool> IsOwner(string publisher)
        => (await Bundles()).Keys.Any(key => key.Owner == publisher);

    public async Task<IReadOnlyList<MarketplaceListing>> Listings()
        => [.. (await Bundles()).Select(entry => new MarketplaceListing(
            entry.Key.Owner, new AppSource.Shipped(), entry.Key.Name,
            entry.Value.Manifest.Title, entry.Value.Manifest.Description, entry.Value.Version, entry.Value.Manifest.Icon, entry.Value.Manifest.Requires))];

    public async Task<FeatureOrigin?> Origin(string owner, string name)
        => (await Snapshot()).Origins.GetValueOrDefault((owner, name));

    // Scripts in the folder, plus every ShippedApps root (a root may now live only as a compiled app).
    public async Task<IReadOnlyList<(string Publisher, string Name)>> BootSet()
    {
        var loaded = await Snapshot();
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var ((owner, name), origin) in loaded.Origins)
        {
            if (owner == ShippedApps.Publisher && origin is FeatureOrigin.Folder) { names.Add(name); }
        }
        foreach (var root in ShippedApps.Roots) { names.Add(root); }
        return [.. names.Select(name => (ShippedApps.Publisher, name))];
    }

    // Writes the bundle into the owner's folder and reloads, so Find and Listings switch to the new
    // contents together. A failed reload leaves the previous snapshot in place.
    public async Task<AppBundle> SaveToSource(string owner, AppBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (_directory is not { Writable: true } directory)
        { throw new InvalidOperationException($"{owner} has no writable features folder to save {bundle.Name} into."); }
        // Only an owner the folder already holds: an origin is brain state, not proof of a features owner.
        if (!await IsOwner(owner)) { throw new InvalidOperationException($"{owner} is not an owner in the features folder."); }
        directory.Write(owner, bundle);
        var reloaded = LoadCombined();
        var loaded = await reloaded;
        if (loaded.Failures.FirstOrDefault(failure => failure.Owner == owner && failure.Name == bundle.Name) is { } failed)
        { throw new InvalidOperationException(failed.Reason); }
        lock (_gate) { _loaded = reloaded; }
        return loaded.Bundles.GetValueOrDefault((owner, bundle.Name))
            ?? throw new InvalidOperationException($"{owner}/{bundle.Name} was written but did not load back.");
    }

    // Reads one feature from the folder again, so an edit on disk reaches Install and Update without a
    // restart. The edited bundle passes the same gate as at boot: one that does not compile throws with
    // its errors and leaves the previous snapshot in place. A feature the folder no longer holds is left as it was.
    public async Task<AppBundle?> Reload(string owner, string name)
    {
        if (string.Equals(owner, SideloadOwner, StringComparison.Ordinal) && _sideload is { } side)
        {
            var developed = side.Scan().Features.Where(feature => feature.Bundle.Name == name).ToArray();
            if (developed.Length > 1)
            { throw new InvalidOperationException($"{owner}/{name} ships twice: {string.Join(", ", developed.Select(copy => copy.Folder))}."); }
            if (developed.Length == 1)
            {
                await RequireCompiles(owner, developed[0].Bundle, _sandbox);
                lock (_gate) { _loaded = Replace(_loaded ?? LoadCombined(), owner, developed[0].Bundle); }
                return developed[0].Bundle;
            }
        }
        if (string.Equals(owner, DevOwner, StringComparison.Ordinal) && _dev is { } dev && System.IO.Directory.Exists(dev.Path))
        {
            var developed = dev.Load().Where(feature => feature.Owner == owner && feature.Bundle.Name == name).ToArray();
            if (developed.Length > 1)
            { throw new InvalidOperationException($"{owner}/{name} ships twice: {string.Join(", ", developed.Select(copy => copy.Folder))}."); }
            if (developed.Length == 1)
            {
                await RequireCompiles(owner, developed[0].Bundle, _sandbox);
                lock (_gate) { _loaded = Replace(_loaded ?? LoadCombined(), owner, developed[0].Bundle); }
                return developed[0].Bundle;
            }
        }
        if (_directory is not { } directory) { return null; }
        var copies = directory.Load().Where(feature => feature.Owner == owner && feature.Bundle.Name == name).ToArray();
        if (copies.Length == 0) { return await Find(owner, name); }
        if (copies.Length > 1)
        { throw new InvalidOperationException($"{owner}/{name} ships twice: {string.Join(", ", copies.Select(copy => copy.Folder))}."); }
        var bundle = copies[0].Bundle;
        await RequireCompiles(owner, bundle, _sandbox);
        lock (_gate) { _loaded = Replace(_loaded ?? LoadCombined(), owner, bundle); }
        return bundle;
    }

    // Publisher of a folder pushed by `intochat run`. Never os, whatever the folder says.
    public const string DevOwner = "local";

    // Publisher of <Home>/apps. Never os, whatever the folder or the spec says. Role is ignored.
    public const string SideloadOwner = "sideload";

    // Compiles through the sandbox, then keeps the bundle. A failure throws and leaves the previous snapshot.
    public async Task<AppBundle> AcceptLocal(AppBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (_dev is not { Writable: true }) { throw new InvalidOperationException("This host has no dev apps folder."); }
        if (_sandbox is null) { throw new InvalidOperationException("The script sandbox is not available."); }
        await RequireCompiles(DevOwner, bundle, _sandbox);
        _dev.Write(DevOwner, bundle);
        lock (_gate) { _loaded = Replace(_loaded ?? LoadCombined(), DevOwner, bundle); }
        return bundle;
    }

    // Compiles through the sandbox, then keeps the bundle under sideload. A failure throws and writes nothing.
    public async Task<AppBundle> AcceptSideload(AppBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (_sideload is null) { throw new InvalidOperationException("This host has no apps directory."); }
        if (_sandbox is null) { throw new InvalidOperationException("The script sandbox is not available."); }
        if (SideloadDirectoryBlocked(bundle.Files) is { } blocked) { throw new InvalidOperationException(blocked); }
        await RequireCompiles(SideloadOwner, bundle, _sandbox);
        _sideload.Write(bundle);
        lock (_gate) { _loaded = Replace(_loaded ?? LoadCombined(), SideloadOwner, bundle); }
        return bundle;
    }

    public async Task RescanSideload()
    {
        if (_sideload is null) { return; }
        if (_git is not null) { await Materialize(_sideload); }
        var added = await LoadSideload(_sideload.Scan(), _sandbox);
        var merged = Merge(await Snapshot(), added, SideloadOwner);
        lock (_gate) { _loaded = Task.FromResult(merged); }
    }

    public async Task<string> AddSideloadGit(GitSideload git, string url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(git);
        if (_sideload is null) { throw new InvalidOperationException("This host has no apps directory."); }
        var name = await _sideload.Adopt(git, url, cancellationToken);
        await RescanSideload();
        return name;
    }

    private async Task Materialize(SideloadDirectory side)
    {
        if (_git is null || !System.IO.Directory.Exists(side.Path)) { return; }
        foreach (var file in System.IO.Directory.EnumerateFiles(side.Path, "*.git-url"))
        {
            try
            {
                await side.Adopt(_git, File.ReadAllText(file).Trim(), CancellationToken.None);
                File.Delete(file);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or IntoChat.OS.Runtime.Storage.Confinement.ConfinementDenied)
            { _ = error.Message; }
        }
    }

    private static string? SideloadDirectoryBlocked(IReadOnlyDictionary<string, string> files)
    {
        var found = DevAppEndpoints.EmptyCatches(files);
        if (found.Length == 0) { return null; }
        var first = found[0];
        return $"{first.Id} {first.Message} ({first.File}:{first.Line})";
    }

    private static async Task<Loaded> Replace(Task<Loaded> previous, string owner, AppBundle bundle)
    {
        var latest = await previous;
        var key = (owner, bundle.Name);
        var bundles = new SortedDictionary<(string Owner, string Name), AppBundle>(latest.Bundles.ToDictionary()) { [key] = bundle };
        var origins = latest.Origins.ToDictionary();
        if (!origins.ContainsKey(key)) { origins[key] = new FeatureOrigin.Folder(owner + "/" + bundle.Name); }
        return new(bundles, [.. latest.Failures.Where(failure => failure.Owner != owner || failure.Name != bundle.Name)], origins);
    }

    internal async Task<IReadOnlyDictionary<(string Owner, string Name), AppBundle>> Bundles() => (await Snapshot()).Bundles;

    private Task<Loaded> Snapshot()
    {
        lock (_gate) { return _loaded ??= LoadCombined(); }
    }

    private async Task<Loaded> LoadCombined()
    {
        Loaded scripts;
        if (_directory is null) { scripts = Nothing; }
        else
        {
            try { scripts = await Load(_directory, _sandbox); }
            catch (DirectoryNotFoundException error)
            { scripts = new(Nothing.Bundles, [new("", "", "", error.Message)], Nothing.Origins); }
        }
        if (_dev is { } dev && System.IO.Directory.Exists(dev.Path))
        {
            Loaded developed;
            try { developed = await Load(dev, _sandbox); }
            catch (DirectoryNotFoundException) { developed = Nothing; }
            var devBundles = scripts.Bundles.ToDictionary();
            var devOrigins = scripts.Origins.ToDictionary();
            List<FeatureFailure> devFailures = [.. scripts.Failures];
            foreach (var (key, bundle) in developed.Bundles)
            {
                if (devBundles.ContainsKey(key))
                {
                    devFailures.Add(new(key.Owner, key.Name, bundle.Version, $"{key.Owner}/{key.Name} ships twice: the features folder and the dev apps folder."));
                    devBundles.Remove(key);
                    devOrigins.Remove(key);
                    continue;
                }
                devBundles[key] = bundle;
                devOrigins[key] = developed.Origins[key];
            }
            devFailures.AddRange(developed.Failures);
            scripts = new(devBundles, [.. devFailures], devOrigins);
        }
        if (_sideload is { } side)
        {
            if (_git is not null) { await Materialize(side); }
            Loaded added;
            try { added = await LoadSideload(side.Scan(), _sandbox); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { added = new(Nothing.Bundles, [new(SideloadOwner, "", "", error.Message)], Nothing.Origins); }
            scripts = Merge(scripts, added, SideloadOwner);
        }
        if (_modules is null) { return scripts; }
        var bundles = new SortedDictionary<(string Owner, string Name), AppBundle>(scripts.Bundles.ToDictionary());
        var origins = scripts.Origins.ToDictionary();
        List<FeatureFailure> failures = [.. scripts.Failures];
        foreach (var compiled in CompiledApps.Of(_modules))
        {
            var key = (compiled.Owner, compiled.Bundle.Name);
            if (bundles.ContainsKey(key))
            {
                var reason = $"{compiled.Owner}/{compiled.Bundle.Name} ships twice: the features folder and {compiled.Origin.Assembly.GetName().Name}.";
                failures.Add(new(compiled.Owner, compiled.Bundle.Name, compiled.Bundle.Version, reason));
                bundles.Remove(key);
                origins.Remove(key);
                continue;
            }
            bundles[key] = compiled.Bundle;
            origins[key] = compiled.Origin;
        }
        return new(bundles, [.. failures], origins);
    }

    private static readonly Loaded Nothing = new(new Dictionary<(string Owner, string Name), AppBundle>(), [], new Dictionary<(string Owner, string Name), FeatureOrigin>());

    private sealed record Loaded(
        IReadOnlyDictionary<(string Owner, string Name), AppBundle> Bundles,
        FeatureFailure[] Failures,
        IReadOnlyDictionary<(string Owner, string Name), FeatureOrigin> Origins);

    private static async Task<Loaded> Load(FeatureDirectory directory, IScriptSandbox? sandbox)
    {
        using var load = Telemetry.StartActivity("features.load");
        load?.SetTag("features.path", directory.Path);
        var bundles = new SortedDictionary<(string Owner, string Name), AppBundle>();
        var origins = new Dictionary<(string Owner, string Name), FeatureOrigin>();
        List<FeatureFailure> failures = [];
        var shipped = directory.Load();
        var twice = shipped.GroupBy(feature => (feature.Owner, feature.Bundle.Name)).Where(group => group.Count() > 1).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var feature in shipped)
        {
            if (twice.TryGetValue((feature.Owner, feature.Bundle.Name), out var copies))
            {
                var reason = $"{feature.Owner}/{feature.Bundle.Name} ships twice: {string.Join(", ", copies.Select(copy => copy.Folder))}.";
                failures.Add(new(feature.Owner, feature.Bundle.Name, feature.Bundle.Version, reason));
                continue;
            }
            using var each = Telemetry.StartActivity("feature.load");
            each?.SetTag("feature.owner", feature.Owner);
            each?.SetTag("feature.name", feature.Bundle.Name);
            each?.SetTag("feature.version", feature.Bundle.Version);
            var key = (feature.Owner, feature.Bundle.Name);
            bundles[key] = feature.Bundle;
            origins[key] = new FeatureOrigin.Folder(feature.Folder);
            try { await RequireCompiles(feature.Owner, feature.Bundle, sandbox); }
            catch (InvalidOperationException error)
            {
                bundles.Remove(key);
                origins.Remove(key);
                failures.Add(new(feature.Owner, feature.Bundle.Name, feature.Bundle.Version, error.Message));
                each?.SetStatus(ActivityStatusCode.Error, error.Message);
            }
        }
        load?.SetTag("features.count", bundles.Count);
        load?.SetTag("features.failed", failures.Count);
        return new(bundles, [.. failures], origins);
    }

    private static async Task<Loaded> LoadSideload(SideloadScan scan, IScriptSandbox? sandbox)
    {
        var bundles = new Dictionary<(string Owner, string Name), AppBundle>();
        var origins = new Dictionary<(string Owner, string Name), FeatureOrigin>();
        var failures = scan.Failures.ToList();
        var twice = scan.Features.GroupBy(feature => feature.Bundle.Name).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var feature in scan.Features)
        {
            if (twice.Contains(feature.Bundle.Name))
            {
                failures.Add(new(feature.Owner, feature.Bundle.Name, feature.Bundle.Version, $"{feature.Owner}/{feature.Bundle.Name} ships twice: {feature.Folder}."));
                continue;
            }
            var key = (feature.Owner, feature.Bundle.Name);
            bundles[key] = feature.Bundle;
            origins[key] = new FeatureOrigin.Folder(feature.Folder);
            try { await RequireCompiles(feature.Owner, feature.Bundle, sandbox); }
            catch (InvalidOperationException error)
            {
                bundles.Remove(key);
                origins.Remove(key);
                failures.Add(new(feature.Owner, feature.Bundle.Name, feature.Bundle.Version, error.Message));
            }
        }
        return new(bundles, [.. failures], origins);
    }

    private static Loaded Merge(Loaded scripts, Loaded added, string owner)
    {
        var bundles = scripts.Bundles.ToDictionary();
        var origins = scripts.Origins.ToDictionary();
        List<FeatureFailure> failures = [.. scripts.Failures.Where(failure => failure.Owner != owner)];
        foreach (var key in bundles.Keys.Where(key => key.Owner == owner).ToArray())
        {
            bundles.Remove(key);
            origins.Remove(key);
        }
        foreach (var (key, bundle) in added.Bundles)
        {
            if (bundles.ContainsKey(key))
            {
                failures.Add(new(key.Owner, key.Name, bundle.Version, $"{key.Owner}/{key.Name} ships twice."));
                bundles.Remove(key);
                origins.Remove(key);
                continue;
            }
            bundles[key] = bundle;
            if (added.Origins.TryGetValue(key, out var origin)) { origins[key] = origin; }
        }
        failures.AddRange(added.Failures);
        return new(bundles, [.. failures], origins);
    }

    // The same gate AppNeuron.Publish applies: the reaction shape always (it is syntax), the compile only
    // where a sandbox can check.
    private static async Task RequireCompiles(string owner, AppBundle bundle, IScriptSandbox? sandbox)
    {
        var scripts = bundle.Files.Where(file => file.Key.EndsWith(".cs", StringComparison.Ordinal))
            .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
        if (ReactionShape.Check(scripts) is { Length: > 0 } shape)
        { throw new InvalidOperationException($"{owner}/{bundle.Name} {bundle.Version} is not shaped as reactions: " + ReactionShape.Describe(shape)); }
        // Compiled as an installed app deploys it (its program files): app.cs is manifest and spec state, not code.
        scripts.Remove(AppFiles.DeclarationPath);
        if (scripts.Count == 0 || sandbox is null) { return; }
        ScriptCompilationCheck check;
        try { check = await sandbox.Compile(scripts, CancellationToken.None); }
        catch (InvalidOperationException) { return; }
        if (check.Success) { return; }
        throw new InvalidOperationException($"{owner}/{bundle.Name} {bundle.Version} does not compile: "
            + string.Join("; ", check.Errors.Select(error => $"{error.File}:{error.Line} {error.Message}")));
    }
}

public sealed record FeatureFailure(string Owner, string Name, string Version, string Reason);
