using IntoChat.OS.Modules.Microsoft.Azure;
using IntoChat.OS.Modules.Microsoft.Azure.Install;

namespace IntoChat.OS.Kernel;

// In-memory IAzureArm driver for testing and local verification of InstallPlan, ManifestEnvironment and ModuleDeployment<T>
// without contacting Azure Resource Manager or starting Docker containers.
public sealed class InMemoryCloud(bool autoSeedVaultSecrets = false) : IAzureArm
{
    private readonly List<ResourceSpec> _ensured = [];
    private readonly Dictionary<string, ResourceRef> _resources = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ResourceSpec> Ensured => _ensured;

    public Dictionary<string, string> Vault { get; } = new(StringComparer.Ordinal);

    public ContainerAppSpec? OsApp => _ensured.OfType<ContainerAppSpec>().LastOrDefault();

    public Task<ResourceRef> Ensure(string subscription, ResourceSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        _ensured.Add(spec);
        if (spec is SecretSpec { External: true } external && !Vault.ContainsKey(external.Name))
        {
            throw new InvalidOperationException($"external secret '{external.Name}' is not set.");
        }
        if (spec is SecretSpec { Generator.Length: > 0 } generated)
        {
            if (!Vault.ContainsKey(generated.Name))
            {
                Vault[generated.Name] = SecretGenerators.Generate(generated.Generator);
            }
        }
        else if (spec is SecretSpec secret && (secret.Overwrite || !Vault.ContainsKey(secret.Name)))
        {
            var names = SecretSpec.ReferencesIn(secret.Value);
            Vault[secret.Name] = SecretSpec.Substitute(secret.Value, names.ToDictionary(name => name, name => Vault[name]));
        }

        var id = ResourceIds.For(subscription, spec);
        var reference = new ResourceRef(id, spec.Kind, spec.Name, spec.ResourceGroup)
        {
            Endpoint = spec switch
            {
                ContainerAppSpec => $"{spec.Name}.inmemory.{spec.Location}.azurecontainerapps.io",
                SessionPoolSpec => $"https://{spec.Name}.inmemory.{spec.Location}.azurecontainerapps.io/",
                RegistrySpec => $"{spec.Name}.azurecr.io",
                _ => "",
            },
        };
        _resources[id] = reference;
        return Task.FromResult(reference);
    }

    public Task<string?> ReadSecret(string vault, string name, CancellationToken ct)
    {
        if (Vault.TryGetValue(name, out var value))
        {
            return Task.FromResult<string?>(value);
        }
        if (autoSeedVaultSecrets)
        {
            Vault[name] = $"in-memory-{name}";
            return Task.FromResult<string?>(Vault[name]);
        }
        return Task.FromResult<string?>(null);
    }

    public Task<ResourceRef?> Read(string subscription, ResourceRef resource, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return Task.FromResult(_resources.GetValueOrDefault(resource.Id));
    }

    public Task Remove(string subscription, ResourceRef resource, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _resources.Remove(resource.Id);
        return Task.CompletedTask;
    }

    public Task<ResourceRef[]> List(string subscription, string tagValue, CancellationToken ct)
        => Task.FromResult(_resources.Values.ToArray());

    public string? CurrentPrincipalId { get; set; }

    public List<(string TargetRegistry, string SourceRegistryResourceId, string SourceImage)> ImportedImages { get; } = [];

    public ValueTask<string> CurrentPrincipalObjectId(CancellationToken ct)
        => ValueTask.FromResult(CurrentPrincipalId ?? "00000000-0000-0000-0000-0000000000aa");

    public Task ImportImage(string subscription, string targetGroup, string targetRegistry, string sourceRegistryResourceId, string sourceImage, CancellationToken ct)
    {
        ImportedImages.Add((targetRegistry, sourceRegistryResourceId, sourceImage));
        return Task.CompletedTask;
    }
}
