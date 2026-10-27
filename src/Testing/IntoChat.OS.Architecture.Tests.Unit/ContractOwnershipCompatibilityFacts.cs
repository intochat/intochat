using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class ContractOwnershipCompatibilityFacts
{
    // Captured from the previous binaries, before moving UiChildRef out of the vocabulary. (RouterDecided's row went with
    // 06-05b: a plain neuron's signal is JSON in the runtime journal, and legacy journals are refused under SAFE-01.)
    [Theory]
    [InlineData("MAHiZFpFGXVpLmNoaWxkLXJlZuhADWxheW91dEErbGVnYWN5L3dvcmtzcGFjZS9yb2904A==",
        "IntoChat.OS.Modules.Flutter.UiChildRef", "Name", "legacy/workspace/root")]
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
        Assert.Equal("layout", Assert.IsType<IntoChat.OS.Modules.Flutter.UiChildRef>(value).Kind);
    }
}
