using System.Collections.Immutable;
using DigitalBrain.Apps.Packaging;

namespace IntoChat.OS.Modules.Apps;

// This inventory contains host-shipped packages. Uploaded packages enter only the package cache,
// never this inventory, so an upload cannot acquire a shipped publisher's trust.
internal sealed class CompiledBuiltinCatalog(string directory, CompiledAppPackages packages) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Loaded? current;

    internal async Task Reload(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { current = await Load(ct); }
        finally { gate.Release(); }
    }

    internal async Task<CompiledBuiltin?> Find(string publisher, string name, CancellationToken ct = default)
        => (await Snapshot(ct)).Entries.Values.SingleOrDefault(entry => entry.Publisher == publisher && entry.Package.Descriptor.Id == name)?.Export();

    internal async Task<IReadOnlyList<CompiledBuiltin>> All(CancellationToken ct = default)
        => [.. (await Snapshot(ct)).Entries.Values.OrderBy(entry => entry.Publisher, StringComparer.Ordinal)
            .ThenBy(entry => entry.Package.Descriptor.Id, StringComparer.Ordinal).Select(entry => entry.Export())];

    internal async Task<IReadOnlyList<FeatureFailure>> Failures(CancellationToken ct = default)
        => (await Snapshot(ct)).Failures;

    private async Task<Loaded> Snapshot(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return current ??= await Load(ct); }
        finally { gate.Release(); }
    }

    private async Task<Loaded> Load(CancellationToken ct)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        List<FeatureFailure> failures = [];
        if (!Directory.Exists(directory)) { return new(entries, []); }
        RequireNotLink(directory);
        foreach (var publisher in new DirectoryInfo(directory).EnumerateDirectories().OrderBy(folder => folder.Name, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            RequireNotLink(publisher.FullName);
            if (publisher.Name.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-'))
            { throw new InvalidDataException($"'{publisher.Name}' is not a compiled feature publisher."); }
            foreach (var file in publisher.EnumerateFiles("*.intochat-app").OrderBy(file => file.Name, StringComparer.Ordinal))
            {
                var previous = current?.Entries.GetValueOrDefault(file.FullName);
                try
                {
                    RequireNotLink(file.FullName);
                    await using var stream = file.OpenRead();
                    var package = await packages.Stage(stream, ct);
                    var manifest = CompiledPackageMetadata.Manifest(package.Descriptor);
                    var files = ImmutableDictionary<string, string>.Empty;
                    if (manifest.Icon is { Length: > 0 } icon)
                    {
                        var payload = package.Descriptor.Payload.SingleOrDefault(entry => entry.Path == icon)
                            ?? throw new InvalidDataException($"The compiled feature does not carry its icon '{icon}'.");
                        if (payload.Length > 1024 * 1024) { throw new InvalidDataException("The compiled feature icon exceeds one MiB."); }
                        files = files.Add(icon, await File.ReadAllTextAsync(Path.Combine(package.DirectoryPath, icon), ct));
                    }
                    entries[file.FullName] = new(publisher.Name, package, files);
                }
                catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // A bad replacement is visible in diagnostics, while the previous known-good artifact remains usable.
                    if (previous is not null) { entries[file.FullName] = previous; }
                    failures.Add(new(publisher.Name, previous?.Package.Descriptor.Id ?? Path.GetFileNameWithoutExtension(file.Name),
                        previous?.Package.Descriptor.Version ?? "", error.Message));
                }
            }
        }
        foreach (var duplicate in entries.GroupBy(item => (item.Value.Publisher, item.Value.Package.Descriptor.Id)).Where(group => group.Count() > 1).ToArray())
        {
            foreach (var item in duplicate)
            {
                entries.Remove(item.Key);
                failures.Add(new(item.Value.Publisher, item.Value.Package.Descriptor.Id, item.Value.Package.Descriptor.Version,
                    $"{item.Value.Publisher}/{item.Value.Package.Descriptor.Id} ships more than once in the compiled inventory."));
            }
        }
        return new(entries, [.. failures]);
    }

    private static void RequireNotLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        { throw new InvalidDataException("Compiled feature inventory paths cannot be links."); }
    }

    private sealed record Loaded(IReadOnlyDictionary<string, Entry> Entries, ImmutableArray<FeatureFailure> Failures);
    private sealed record Entry(string Publisher, ValidatedAppPackage Package, ImmutableDictionary<string, string> Files)
    {
        internal CompiledBuiltin Export() => new(Publisher, Package, new(Package.Descriptor.Id, Package.Descriptor.Version,
            CompiledPackageMetadata.Manifest(Package.Descriptor), Package.Descriptor.Presentation.GetValueOrDefault("spec", ""), new(Files, StringComparer.Ordinal)));
    }

    public void Dispose() => gate.Dispose();
}

internal sealed record CompiledBuiltin(string Publisher, DigitalBrain.Apps.Packaging.ValidatedAppPackage Package, AppBundle Bundle);
