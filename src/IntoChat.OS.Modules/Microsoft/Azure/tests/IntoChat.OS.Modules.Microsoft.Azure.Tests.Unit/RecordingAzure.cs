using IntoChat.OS.Microsoft.Azure;

namespace IntoChat.OS.Modules.Microsoft.Azure.Tests.Unit;

internal sealed class RecordingAzure : IAzureArm
{
    public List<ResourceSpec> Ensured { get; } = [];

    // The vault as the data plane sees it: values by secret name, and the secrets read to build another value.
    public Dictionary<string, string> Vault { get; } = [];

    public List<string> VaultReads { get; } = [];

    public Task<ResourceRef> Ensure(string subscription, ResourceSpec spec, CancellationToken ct)
    {
        Ensured.Add(spec);
        if (spec is SecretSpec { External: true } external && !Vault.ContainsKey(external.Name))
        {
            throw new InvalidOperationException($"external secret '{external.Name}' is not set.");
        }
        if (spec is SecretSpec { Generator.Length: > 0 } generated)
        {
            if (!Vault.ContainsKey(generated.Name)) { Vault[generated.Name] = SecretGenerators.Generate(generated.Generator); }
        }
        else if (spec is SecretSpec secret && (secret.Overwrite || !Vault.ContainsKey(secret.Name)))
        {
            var names = SecretSpec.ReferencesIn(secret.Value);
            VaultReads.AddRange(names);
            Vault[secret.Name] = SecretSpec.Substitute(secret.Value, names.ToDictionary(name => name, name => Vault[name]));
        }
        return Task.FromResult(new ResourceRef($"/subscriptions/{subscription}/resourceGroups/{spec.ResourceGroup}/{spec.Kind}/{spec.Name}", spec.Kind, spec.Name, spec.ResourceGroup)
        {
            Endpoint = spec is SessionPoolSpec ? $"https://{spec.Name}.env-123.{spec.Location}.azurecontainerapps.io/" : "",
        });
    }

    public Task<string?> ReadSecret(string vault, string name, CancellationToken ct) => Task.FromResult(Vault.GetValueOrDefault(name));

    public Task<ResourceRef?> Read(string subscription, ResourceRef resource, CancellationToken ct) => Task.FromResult<ResourceRef?>(resource);

    public Task Remove(string subscription, ResourceRef resource, CancellationToken ct) => Task.CompletedTask;

    public Task<ResourceRef[]> List(string subscription, string tagValue, CancellationToken ct) => Task.FromResult<ResourceRef[]>([]);
}

internal sealed class FakeState<T> : IPersistentState<T> where T : new()
{
    public T State { get; set; } = new();
    public string Etag => "";
    public bool RecordExists => true;
    public Task ClearStateAsync() => Task.CompletedTask;
    public Task WriteStateAsync() => Task.CompletedTask;
    public Task ReadStateAsync() => Task.CompletedTask;
}
