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
        => (await Snapshot(ct)).Entries.Values.SingleOrDefault(entry => entry.Publisher == publisher && entry.Bundle.Name == name)?.Export();

    internal async Task<IReadOnlyList<CompiledBuiltin>> All(CancellationToken ct = default)
        => [.. (await Snapshot(ct)).Entries.Values.OrderBy(entry => entry.Publisher, StringComparer.Ordinal)
            .ThenBy(entry => entry.Bundle.Name, StringComparer.Ordinal).Select(entry => entry.Export())];

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
                    entries[file.FullName] = new(publisher.Name, package, await BundleOf(package, ct));
                }
                catch (InvalidDataException error) { Refuse(error); }
                catch (IOException error) { Refuse(error); }
                catch (UnauthorizedAccessException error) { Refuse(error); }
                catch (ArgumentException error) { Refuse(error); }

                // A bad replacement is visible in diagnostics, while the previous known-good artifact remains usable.
                void Refuse(Exception error)
                {
                    if (previous is not null) { entries[file.FullName] = previous; }
                    failures.Add(new(publisher.Name, previous?.Bundle.Name ?? Path.GetFileNameWithoutExtension(file.Name),
                        previous?.Bundle.Version ?? "", error.Message));
                }
            }
        }
        foreach (var duplicate in entries.GroupBy(item => (item.Value.Publisher, item.Value.Bundle.Name)).Where(group => group.Count() > 1).ToArray())
        {
            foreach (var item in duplicate)
            {
                entries.Remove(item.Key);
                failures.Add(new(item.Value.Publisher, item.Value.Bundle.Name, item.Value.Bundle.Version,
                    $"{item.Value.Publisher}/{item.Value.Bundle.Name} ships more than once in the compiled inventory."));
            }
        }
        return new(entries, [.. failures]);
    }

    // A program package is a feature again (its declaration and sources); any other package lists by its inert presentation and carries only its icon.
    private static async Task<AppBundle> BundleOf(ValidatedAppPackage package, CancellationToken ct)
    {
        if (CompiledPackageMetadata.IsProgram(package.Descriptor)) { return CompiledPackageMetadata.ProgramBundle(package); }
        var manifest = CompiledPackageMetadata.Manifest(package.Descriptor);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (manifest.Icon is { Length: > 0 } icon)
        {
            var payload = package.Descriptor.Payload.SingleOrDefault(entry => entry.Path == icon)
                ?? throw new InvalidDataException($"The compiled feature does not carry its icon '{icon}'.");
            if (payload.Length > 1024 * 1024) { throw new InvalidDataException("The compiled feature icon exceeds one MiB."); }
            files[icon] = await File.ReadAllTextAsync(Path.Combine(package.DirectoryPath, icon), ct);
        }
        return new(package.Descriptor.Id, package.Descriptor.Version, manifest, package.Descriptor.Presentation.GetValueOrDefault("spec", ""), files);
    }

    private static void RequireNotLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        { throw new InvalidDataException("Compiled feature inventory paths cannot be links."); }
    }

    private sealed record Loaded(IReadOnlyDictionary<string, Entry> Entries, ImmutableArray<FeatureFailure> Failures);
    private sealed record Entry(string Publisher, ValidatedAppPackage Package, AppBundle Bundle)
    {
        // A caller owns what it gets: the manifest is derived again and the files copied, so a mutation never reaches the next review.
        internal CompiledBuiltin Export() => new(Publisher, Package, Bundle with { Manifest = CompiledPackageMetadata.Manifest(Package), Files = new(Bundle.Files, StringComparer.Ordinal) });
    }

    public void Dispose() => gate.Dispose();
}

internal sealed record CompiledBuiltin(string Publisher, DigitalBrain.Apps.Packaging.ValidatedAppPackage Package, AppBundle Bundle);
