using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Storage;

namespace IntoChat.OS.Architecture.Tests.Unit;

// A rolling cluster runs two builds at once, so a state written by the newer build is read by the
// older one and the reverse. Storage serializes with the Orleans serializer, which keys fields by [Id].
public sealed class AdditiveStateFacts
{
    [Fact]
    public void StateWrittenWithANewFieldIsReadByTheOldShape()
    {
        using var provider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        IGrainStorageSerializer storage = new OrleansGrainStorageSerializer(provider.GetRequiredService<Serializer>());

        var older = storage.Deserialize<V1>(storage.Serialize(new V2("a", "b")));
        var newer = storage.Deserialize<V2>(storage.Serialize(new V1("a")));

        Assert.Equal("a", older!.A);
        Assert.Equal("a", newer!.A);
        Assert.Null(newer.B);
    }

    [GenerateSerializer, Alias("tests.additive-state.v1")]
    public sealed record V1([property: Id(0)] string A);

    [GenerateSerializer, Alias("tests.additive-state.v2")]
    public sealed record V2([property: Id(0)] string A, [property: Id(1)] string? B);
}
