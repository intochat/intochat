using IntoChat.OS.Modules.Microsoft.Azure;
using IntoChat.OS.Modules.Microsoft.Azure.Install;

namespace IntoChat.OS.Kernel;

/// <summary>
/// Local Aspire/Docker <see cref="IAzureArm"/> provisioning driver. Translates the same <see cref="InstallPlan"/>,
/// <see cref="ManifestEnvironment"/>, and <see cref="ModuleDeployment{T}"/> specifications used by Azure Cloud
/// and <see cref="InMemoryCloud"/> into local Docker container specs, local endpoints, and resolved host environment variables.
/// </summary>
public sealed class AspireCloud(string localHost = "localhost", int basePort = 18080, bool autoSeedVaultSecrets = true) : IAzureArm
{
    private readonly List<ResourceSpec> _ensured = [];
    private readonly List<LocalContainerBinding> _containers = [];
    private readonly Dictionary<string, ResourceRef> _resources = new(StringComparer.OrdinalIgnoreCase);
    private int _nextPort = basePort;

    public IReadOnlyList<ResourceSpec> Ensured => _ensured;

    public IReadOnlyList<LocalContainerBinding> LocalContainers => _containers;

    public Dictionary<string, string> Vault { get; } = new(StringComparer.Ordinal);

    public ContainerAppSpec? OsApp => _ensured.OfType<ContainerAppSpec>().LastOrDefault();

    /// <summary>
    /// All environment variables and resolved Key Vault secret references for the OS host in local Aspire/Docker execution.
    /// </summary>
    public IReadOnlyDictionary<string, string> ResolvedEnvironment
    {
        get
        {
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            if (OsApp is not { } app) { return env; }
            foreach (var (key, value) in app.Variables)
            {
                env[key] = value;
            }
            foreach (var secret in app.Secrets)
            {
                if (Vault.TryGetValue(secret.VaultSecret, out var secretValue))
                {
                    env[secret.Variable] = secretValue;
                }
            }
            return env;
        }
    }

    public Task<ResourceRef> Ensure(string subscription, ResourceSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        _ensured.Add(spec);

        if (spec is SecretSpec { External: true } external && !Vault.ContainsKey(external.Name))
        {
            Vault[external.Name] = autoSeedVaultSecrets
                ? $"aspire-local-{external.Name}"
                : throw new InvalidOperationException($"external secret '{external.Name}' is not set.");
        }
        else if (spec is SecretSpec { Generator.Length: > 0 } generated)
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

        var endpoint = "";
        if (spec is SessionPoolSpec pool)
        {
            var port = _nextPort++;
            endpoint = $"http://{localHost}:{port}/";
            _containers.Add(new LocalContainerBinding(pool.Name, pool.Image, port, pool.TargetPort, pool.MaxSessions, pool.ReadySessions));
        }
        else if (spec is RegistrySpec)
        {
            endpoint = $"{localHost}:5000";
        }

        var id = ResourceIds.For(subscription, spec);
        var reference = new ResourceRef(id, spec.Kind, spec.Name, spec.ResourceGroup)
        {
            Endpoint = endpoint,
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
            Vault[name] = $"aspire-local-{name}";
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
}

public sealed record LocalContainerBinding(
    string Name,
    string Image,
    int HostPort,
    int TargetPort,
    int MaxSessions,
    int ReadySessions);
