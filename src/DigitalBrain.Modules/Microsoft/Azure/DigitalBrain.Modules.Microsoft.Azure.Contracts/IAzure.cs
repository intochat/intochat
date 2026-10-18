using System.ComponentModel;

namespace DigitalBrain.Microsoft.Azure;

// One Azure subscription, keyed {brain}/connection/azure/{subscriptionId}. Ensure is an idempotent PUT per kind.
[Alias("azure"), Orleans.Metadata.DefaultGrainType("azure")]
public interface IAzure : INeuron
{
    [Description("Create or update one Azure resource from its spec and return its reference; a second call with the same spec changes nothing.")]
    Task<ResourceRef> Ensure(ResourceSpec spec);

    [Description("Read one Azure resource, or nothing when it does not exist.")]
    Task<ResourceRef?> Read(ResourceRef resource);

    [Description("Delete one Azure resource.")]
    Task Remove(ResourceRef resource);

    [Description("The ensured resources whose env or owner tag equals the value.")]
    Task<ResourceRef[]> Inventory(string tagValue);
}

[GenerateSerializer, Alias("azure.ref")]
public sealed record ResourceRef(
    [property: Id(0)] string Id,
    [property: Id(1)] string Kind,
    [property: Id(2)] string Name,
    [property: Id(3)] string ResourceGroup);
