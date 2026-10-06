using System.Reflection;
using DigitalBrain.Framework;
using Orleans;

namespace DigitalBrain.Framework.Tests.Unit;

public sealed class RegistryContractFacts
{
    [Theory]
    [InlineData("RegistryInventory", "registry.inventory", "Records,Domains,Errors")]
    [InlineData("NeuronRecord", "registry.neuron-record", "Domain,TypeId,Key,Display")]
    public void InventoryWireIdentityAndMemberOrderAreStable(string name, string alias, string members)
    {
        var type = typeof(RegistryDiscovery).Assembly.GetType("DigitalBrain.Framework." + name);
        Assert.NotNull(type);
        Assert.Equal(alias, type.GetCustomAttribute<AliasAttribute>()?.Alias);
        Assert.NotNull(type.GetCustomAttribute<GenerateSerializerAttribute>());
        var properties = type.GetProperties().Where(p => p.GetCustomAttribute<IdAttribute>() is not null)
            .OrderBy(p => p.GetCustomAttribute<IdAttribute>()!.Id).ToArray();
        Assert.Equal(members.Split(','), properties.Select(p => p.Name));
        Assert.Equal(Enumerable.Range(0, properties.Length).Select(i => (uint)i), properties.Select(p => p.GetCustomAttribute<IdAttribute>()!.Id));
        if (name == "RegistryInventory") { Assert.All(properties, property => Assert.True(property.PropertyType.IsArray)); }
    }
}
