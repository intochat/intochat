using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class ContractOwnershipCompatibilityFacts
{
    // Captured from the previous binaries, before moving UiChildRef out of the
    // vocabulary. RouterDecided must retain its implicit positional serialization layer.
    [Theory]
    [InlineData("MAHiZFpFGXVpLmNoaWxkLXJlZuhADWxheW91dEErbGVnYWN5L3dvcmtzcGFjZS9yb2904A==",
        "IntoChat.OS.Flutter.UiChildRef", "Name", "legacy/workspace/root")]
    [InlineData("MAGzlAOBI2FpLnJvdXRlci5kZWNpZGVk6CDowAHgwQHoQBtsZWdhY3ktcm91dGVyAR3o4A==",
        "IntoChat.OS.AI.Routing.RouterDecided", "Router", "legacy-router")]
    public void ExistingPayloadsSurviveContractOwnershipChanges(string payload, string type, string member, string expected)
    {
        var services = new ServiceCollection();
        services.AddSerializer(builder =>
        {
            foreach (var assembly in ProductionArchitecture.Assemblies) { builder.AddAssembly(assembly); }
        });
        using var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<Serializer>();
        var value = serializer.Deserialize<object>(Convert.FromBase64String(payload));
        Assert.NotNull(value);
        Assert.Equal(type, value.GetType().FullName);
        Assert.Equal(expected, value.GetType().GetProperty(member)!.GetValue(value));
        var reloaded = serializer.Deserialize<object>(serializer.SerializeToArray(value));
        Assert.NotNull(reloaded);
        Assert.Equal(expected, reloaded.GetType().GetProperty(member)!.GetValue(reloaded));
        if (value is IntoChat.OS.Flutter.UiChildRef child) { Assert.Equal("layout", child.Kind); }
        else { Assert.Equal(7, value.GetType().GetProperty("Count")!.GetValue(value)); }
    }
}
