using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Apps;

// Where a listed app's bundle is read from: a person's brain, or the catalog shipped in the image.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$source")]
[JsonDerivedType(typeof(Brain), "brain")]
[JsonDerivedType(typeof(Shipped), "shipped")]
public abstract record AppSource
{
    public sealed record Brain(string AccountId, string BrainId) : AppSource;

    public sealed record Shipped : AppSource;
}
