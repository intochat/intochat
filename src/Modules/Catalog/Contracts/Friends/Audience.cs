using System.Text.Json.Serialization;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Modules.Apps;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Public), "public")]
[JsonDerivedType(typeof(Friends), "friends")]
[JsonDerivedType(typeof(Only), "only")]
public abstract record Audience
{
    public sealed record Public : Audience;

    public sealed record Friends : Audience;

    public sealed record Only(AccountId[] Accounts) : Audience;

    public static Audience FromLegacy(string? circle) => circle?.Trim().ToLowerInvariant() switch
    {
        null or "" or "public" => new Public(),
        "friends" => new Friends(),
        _ => new Only([]),
    };
}
