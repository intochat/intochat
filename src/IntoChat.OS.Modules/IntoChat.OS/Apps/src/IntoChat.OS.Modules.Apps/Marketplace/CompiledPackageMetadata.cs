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
            return manifest.Validated();
        }
        catch (JsonException error) { throw new InvalidDataException("The package's presentation metadata is invalid.", error); }
    }

    private static string Key(string operation) => operation.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
}
