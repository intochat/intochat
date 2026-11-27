using System.Globalization;

namespace IntoChat.OS.Modules.Microsoft.Azure;

// The shared resources of the environment. RegistryServer is the login server of the registry the installer
// created (or the config's registryServer when it creates none). Registry and Postgres are null when disabled.
public sealed record FoundationRefs(
    ResourceRef ResourceGroup,
    ResourceRef Identity,
    ResourceRef Environment,
    ResourceRef StorageAccount,
    ResourceRef KeyVault,
    string RegistryServer,
    ResourceRef? Registry = null,
    ResourceRef? Postgres = null);

// The module's own entries of the install config, keyed by setting name.
public sealed class ModuleSettings(IReadOnlyDictionary<string, string> values)
{
    public static ModuleSettings Empty { get; } = new(new Dictionary<string, string>());

    public string? Get(string name) => values.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    public string Require(string name) => Get(name) ?? throw new InvalidOperationException($"The module setting '{name}' is required.");

    public int? GetInt32(string name) => Get(name) is { } text
        ? int.Parse(text, CultureInfo.InvariantCulture)
        : null;
}
