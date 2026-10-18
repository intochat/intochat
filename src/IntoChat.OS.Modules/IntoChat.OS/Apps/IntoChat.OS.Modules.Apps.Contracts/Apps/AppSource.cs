using System.Text.Json.Serialization;

namespace IntoChat.OS.Apps;

// Where a listed app's bundle is read from: a person's brain, or the catalog shipped in the image.
[GenerateSerializer, Alias("apps.source")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$source")]
[JsonDerivedType(typeof(Brain), "brain")]
[JsonDerivedType(typeof(Shipped), "shipped")]
public abstract record AppSource
{
    [GenerateSerializer, Alias("apps.source.brain")]
    public sealed record Brain([property: Id(0)] string AccountId, [property: Id(1)] string BrainId) : AppSource;

    [GenerateSerializer, Alias("apps.source.shipped")]
    public sealed record Shipped : AppSource;
}
