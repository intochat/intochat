using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed partial class CollectionCompatibilityFacts
{
    [Fact]
    public void PreviouslyStoredCollectionsRemainReadable()
    {
        var samples = JsonSerializer.Deserialize<Sample[]>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "CollectionCompatibilitySamples.json")))!;
        using var provider = BuildProvider();
        var serializer = provider.GetRequiredService<Serializer>();
        var options = new JsonSerializerOptions { IgnoreReadOnlyProperties = true };
        Assert.Equal(36, samples.Length);
        Assert.All(samples, sample =>
        {
            var value = serializer.Deserialize<object>(Convert.FromBase64String(sample.Payload));
            Assert.NotNull(value);
            Assert.Equal(sample.Type, value.GetType().FullName);
            var property = value.GetType().GetProperty(sample.Member)!;
            Assert.Equal(sample.Expected, JsonSerializer.Serialize(property.GetValue(value), property.PropertyType, options));
            var reloaded = serializer.Deserialize<object>(serializer.SerializeToArray(value));
            Assert.Equal(sample.Expected, JsonSerializer.Serialize(property.GetValue(reloaded), property.PropertyType, options));
        });
    }

    internal static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSerializer(builder =>
        {
            foreach (var assembly in ProductionArchitecture.Assemblies) { builder.AddAssembly(assembly); }
        });
        return services.BuildServiceProvider();
    }

    private sealed record Sample(string Type, string Member, string Representation, string Payload, string Expected);
}
