using DigitalBrain.Microsoft.Azure;

namespace DigitalBrain.Modules.Microsoft.Azure.Tests.Unit;

internal sealed class RecordingAzure : IAzureArm
{
    public List<ResourceSpec> Ensured { get; } = [];

    public Task<ResourceRef> Ensure(string subscription, ResourceSpec spec, CancellationToken ct)
    {
        Ensured.Add(spec);
        return Task.FromResult(new ResourceRef($"/subscriptions/{subscription}/resourceGroups/{spec.ResourceGroup}/{spec.Kind}/{spec.Name}", spec.Kind, spec.Name, spec.ResourceGroup));
    }

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
