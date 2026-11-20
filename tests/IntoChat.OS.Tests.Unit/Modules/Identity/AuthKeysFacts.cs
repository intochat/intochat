using System.Reflection;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Authority;

// AuthKeys names where AuthOptions binds from, so hosts write the keys without seeing the options (or the password):
// every key is a property of AuthOptions under AuthKeys.SectionName, and every AuthOptions property has its key.
public sealed class AuthKeysFacts
{
    private static readonly Dictionary<string, string> Keys = typeof(AuthKeys)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.Name != nameof(AuthKeys.SectionName))
        .ToDictionary(field => field.Name, field => (string)field.GetRawConstantValue()!, StringComparer.Ordinal);

    private static readonly string[] Properties = typeof(AuthOptions)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void EveryKeyIsTheSectionAndThePropertyOfTheSameName()
    {
        Assert.All(Keys, entry => Assert.Equal(AuthKeys.SectionName + ":" + entry.Key, entry.Value));
    }

    [Fact]
    public void KeysAndOptionsNameTheSameSettings()
    {
        Assert.Equal(Properties, Keys.Keys.Order(StringComparer.Ordinal).ToArray());
    }
}
