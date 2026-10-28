using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain.Apps.Packaging;

namespace IntoChat.OS.Modules.Apps;

internal static class CompiledPackageMetadata
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    // A program package (every built-in, and a sideloaded script app) declares itself the way a feature folder does: its
    // files/app.cs.src is the declaration. It carries no inert presentation keys, so its manifest is read from that file, never run.
    internal static bool IsProgram(CompiledAppDescriptor descriptor)
        => descriptor.HostMetadata.GetValueOrDefault("kind") == "program";

    internal static AppManifest Manifest(ValidatedAppPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return IsProgram(package.Descriptor) ? Declared(package).Manifest : Manifest(package.Descriptor);
    }

    // The package's files as a feature bundle: .cs.src sources are the app's program files again, so Edit, Engineer and SelfImprove
    // read them like any installed app's sources, and the declaration gives the name, version, manifest and spec.
    internal static AppBundle ProgramBundle(ValidatedAppPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var declared = Declared(package);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var payload in package.Descriptor.Payload.Where(entry => entry.Path.StartsWith("files/", StringComparison.Ordinal)))
        {
            var relative = payload.Path["files/".Length..];
            if (relative == AppFiles.DeclarationPath + ".src") { continue; }
            if (relative.EndsWith(".src", StringComparison.Ordinal)) { relative = relative[..^".src".Length]; }
            files[relative] = File.ReadAllText(Path.Combine(package.DirectoryPath, payload.Path.Replace('/', Path.DirectorySeparatorChar)))
                .Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        return new(declared.Name, declared.Version, declared.Manifest, declared.Spec, files);
    }

    private static AppDeclarationContent Declared(ValidatedAppPackage package)
    {
        var path = Path.Combine(package.DirectoryPath, "files", AppFiles.DeclarationPath + ".src");
        if (!File.Exists(path)) { throw new InvalidDataException("The program package does not carry its app declaration."); }
        return AppDeclaration.Read(File.ReadAllText(path));
    }

    // Receiving-side review only reads metadata. The trusted host later reconciles this with executable declarations.
    internal static AppManifest Manifest(CompiledAppDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        try
        {
            if (!descriptor.Presentation.TryGetValue("operations", out var encoded))
            { throw new InvalidDataException("The package does not declare its operations."); }
            var operations = JsonSerializer.Deserialize<string[]>(encoded, Json)
                ?? throw new InvalidDataException("The package operations are missing.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var operation in operations)
            {
                if (string.IsNullOrWhiteSpace(operation) || !names.Add(Key(operation)))
                { throw new InvalidDataException("The package declares an empty or ambiguous operation."); }
            }
            if (names.Count == 0) { throw new InvalidDataException("An app must declare at least one operation."); }
            var manifest = descriptor.HostMetadata.TryGetValue("intochat.os.v1", out var extension)
                ? JsonSerializer.Deserialize<AppManifest>(extension, Json) ?? throw new InvalidDataException("The OS manifest is missing.")
                : new AppManifest(descriptor.Name, "", [.. operations.Select(name => new AppOperation(name, name))], [], [],
                    Requires: new(Capabilities: [.. descriptor.Capabilities]));
            if (manifest.Operations is null || manifest.Settings is null || manifest.Slots is null
                || manifest.Operations.Any(operation => operation is null) || manifest.Settings.Any(setting => setting is null)
                || manifest.Slots.Any(slot => slot is null) || manifest.Toolbar?.Any(entry => entry is null) == true)
            { throw new InvalidDataException("The OS manifest contains missing entries."); }
            var exposed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var operation in manifest.Operations)
            {
                if (string.IsNullOrWhiteSpace(operation.Name) || !names.Contains(Key(operation.Name)) || !exposed.Add(Key(operation.Name)))
                { throw new InvalidDataException("The OS manifest exposes an unknown or ambiguous operation."); }
            }
            if (!(manifest.Requires?.Capabilities ?? []).Order(StringComparer.Ordinal).SequenceEqual(descriptor.Capabilities.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            { throw new InvalidDataException("The OS manifest changes the framework capability requirements."); }
            foreach (var slot in manifest.Slots)
            {
                if (slot.Connector.Length > 0 && !(manifest.Requires?.Connectors ?? []).Contains(slot.Connector, StringComparer.Ordinal))
                { throw new InvalidDataException($"Slot '{slot.Name}' binds module '{slot.Connector}', which Requires does not list."); }
            }
            return manifest.Validated();
        }
        catch (JsonException error) { throw new InvalidDataException("The package's presentation metadata is invalid.", error); }
    }

    private static string Key(string operation) => operation.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
}
