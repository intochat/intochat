using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Sdk;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IntoChat.OS.Framework.Tests.Integration;

public sealed class ModuleOptionsBindingFacts
{
    private sealed class RequiredOptions : IModuleOptions
    {
        [Setting(Required = true)] public Uri? Endpoint { get; set; }
        [Setting] public string? Label { get; set; }
        public void Validate() { }
    }

    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder().AddInMemoryCollection(
            pairs.ToDictionary(p => IntoChatOSConfiguration.ModuleOptionsSection("req") + ":" + p.Key, p => (string?)p.Value)).Build();

    [Fact]
    public void AMissingRequiredSettingThrowsNamingTheKey()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Config(("Label", "secret-looking-value")).GetModuleOptions<RequiredOptions>("req"));
        Assert.Contains(IntoChatOSConfiguration.ModuleOptionsSection("req") + ":Endpoint", ex.Message);
        Assert.DoesNotContain("secret-looking-value", ex.Message);
    }

    [Fact]
    public void APresentRequiredSettingBinds()
    {
        var options = Config(("Endpoint", "https://x.example/")).GetModuleOptions<RequiredOptions>("req");
        Assert.Equal(new Uri("https://x.example/"), options.Endpoint);
    }

    [Fact]
    public void TheSecretAttributeCannotDecorateAProperty()
    {
        var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(typeof(SecretAttribute), typeof(AttributeUsageAttribute))!;
        Assert.Equal(AttributeTargets.Class, usage.ValidOn);
    }
}
