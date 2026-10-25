using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using IntoChat.OS.Connections;

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
        var ex = Assert.Throws<ModuleValidationException>(() => Config(("Label", "secret-looking-value")).GetModuleOptions<RequiredOptions>("req"));
        Assert.Equal(IntoChatOSConfiguration.ModuleOptionsSection("req") + ":Endpoint", Assert.Single(ex.Issues).Path);
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

    private sealed class BoundModule() : Module<RequiredOptions>(new ModuleId("req"))
    {
        public override void Configure(ISiloBuilder silo) { }
    }

    private sealed class PlainModule() : Module(new ModuleId("plain"))
    {
        public override void Configure(ISiloBuilder silo) { }
    }

    private static ServiceProvider Host(Module module, params (string Key, string Value)[] pairs)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Config(pairs));
        ModuleOptionsBinding.Add(services, module);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AnOptionsModuleGetsItsOptionsBoundFromItsSection()
    {
        using var provider = Host(new BoundModule(), ("Endpoint", "https://x.example/"), ("Label", "a"));
        var options = provider.GetRequiredService<IOptions<RequiredOptions>>().Value;
        Assert.Equal(new Uri("https://x.example/"), options.Endpoint);
        Assert.Equal("a", options.Label);
    }

    [Fact]
    public void InvalidOptionsFailWithTheModuleValidationException()
    {
        using var provider = Host(new BoundModule(), ("Label", "a"));
        Assert.Throws<ModuleValidationException>(() => provider.GetRequiredService<IOptions<RequiredOptions>>().Value);
    }

    [Fact]
    public void InvalidOptionsAreValidatedOnStart()
    {
        var services = new ServiceCollection();
        ModuleOptionsBinding.Add(services, new BoundModule());
        Assert.Contains(services, d => d.ServiceType.ToString().Contains("StartupValidator", StringComparison.Ordinal));
    }

    [Fact]
    public void APlainModuleRegistersNoOptions()
    {
        var services = new ServiceCollection();
        ModuleOptionsBinding.Add(services, new PlainModule());
        Assert.Empty(services);
    }

    [Theory]
    [InlineData(IntoChatOSConfiguration.ModulesSectionName + ":ai:Options:X", true)]
    [InlineData("intochatos:modules:ai:options:X", true)]
    [InlineData(IntoChatOSConfiguration.ModulesSectionName + ":0:Options:X", false)]
    [InlineData(IntoChatOSConfiguration.ModulesSectionName + ":a__b:Options:X", false)]
    [InlineData("intochatos:Other:ai:Options", false)]
    public void TheOptionsKeyPredicateMatchesOnlyAModulesOwnOptions(string key, bool expected)
        => Assert.Equal(expected, ModuleSettingsValidation.IsOptionsKey(key));
}
