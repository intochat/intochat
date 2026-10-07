using DigitalBrain;
using DigitalBrain.Framework;
using Microsoft.Extensions.Configuration;
using Orleans.Hosting;
using Xunit;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Framework.Tests.Unit;

public sealed class ModuleOptionsFacts
{
    private sealed class FakeOptions : IModuleOptions
    {
        public Uri Endpoint { get; set; } = new("https://default.example/");
        public Dictionary<string, string> Bindings { get; set; } = [];
        public bool Flag { get; set; }
        public void Validate() { if (!Endpoint.IsAbsoluteUri) { throw new ArgumentException("Endpoint must be absolute."); } }
    }

    private sealed class FakeModule() : Module<FakeOptions>(new(nameof(FakeModule)))
    {
        public override void Configure(ISiloBuilder silo) { }
    }

    [Fact]
    public void OptionsSurviveTheCompileAndBindRoundTripByValue()
    {
        var options = new FakeOptions { Endpoint = new("https://x.example/api"), Flag = true, Bindings = { ["a"] = "1" } };
        var definition = ModuleOptionsSerialization.Compile<FakeModule, FakeOptions>(options);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(definition.Settings).Build();
        var bound = configuration.GetModuleOptions<FakeOptions>(nameof(FakeModule));
        Assert.Equal(options.Endpoint, bound.Endpoint);
        Assert.True(bound.Flag);
        Assert.Equal("1", bound.Bindings["a"]);
    }

    [Fact]
    public void ValidationRunsAtCompileAndAtBind()
    {
        Assert.Throws<ArgumentException>(() =>
            ModuleOptionsSerialization.Compile<FakeModule, FakeOptions>(new() { Endpoint = new Uri("/relative", UriKind.Relative) }));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [DigitalBrainConfiguration.ModuleOptionsSection(nameof(FakeModule)) + ":Endpoint"] = "/relative" }).Build();
        Assert.Throws<ArgumentException>(() => configuration.GetModuleOptions<FakeOptions>(nameof(FakeModule)));
    }

    [Fact]
    public void AbsentOptionsBindToValidatedDefaults()
    {
        var configuration = new ConfigurationBuilder().Build();
        Assert.Equal(new Uri("https://default.example/"), configuration.GetModuleOptions<FakeOptions>(nameof(FakeModule)).Endpoint);
    }

    [Fact]
    public void CompiledOptionsPassPublicSettingsValidation()
    {
        var definition = ModuleOptionsSerialization.Compile<FakeModule, FakeOptions>(new());
        ModuleSettingsValidation.ValidatePublicSettings([definition]);
    }

    [Fact]
    public void TheModuleListBindsBesideAnOptionsKeyUnderTheSameSection()
    {
        var options = ModuleOptionsSerialization.Compile<FakeModule, FakeOptions>(new() { Flag = true });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(options.Settings)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DigitalBrainConfiguration.ModulesSectionName + ":0"] = "First.Module, First",
            }).Build();
        Assert.True(configuration.GetModuleOptions<FakeOptions>(nameof(FakeModule)).Flag);
    }

    [Fact]
    public void PopulateCopiesBoundOptionsOntoAnExistingInstance()
    {
        var definition = ModuleOptionsSerialization.Compile<FakeModule, FakeOptions>(new() { Flag = true, Endpoint = new("https://x.example/") });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(definition.Settings).Build();
        var target = new FakeOptions();
        configuration.PopulateModuleOptions(nameof(FakeModule), target);
        Assert.True(target.Flag);
        Assert.Equal(new Uri("https://x.example/"), target.Endpoint);
    }

    [Fact]
    public void BindRefusesAValueTheOptionTypeCannotConvert()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [DigitalBrainConfiguration.ModuleOptionsSection(nameof(FakeModule)) + ":Flag"] = "not-a-bool" }).Build();
        var failure = Assert.Throws<InvalidOperationException>(() => configuration.GetModuleOptions<FakeOptions>(nameof(FakeModule)));
        Assert.Contains(nameof(FakeModule), failure.Message);
    }

    [Theory]
    [InlineData(DigitalBrainConfiguration.ModulesSectionName + ":0:Options")]
    [InlineData(DigitalBrainConfiguration.ModulesSectionName + ":Options")]
    [InlineData(DigitalBrainConfiguration.ModulesSectionName + ":A:B:Options")]
    [InlineData(DigitalBrainConfiguration.ModulesSectionName + ":A__B:Options")]
    public void OnlyTheOptionsKeyShapeIsExemptFromHostOwnedSettings(string key)
    {
        var definition = new ModuleDefinition(new FakeModule(), new Dictionary<string, string?> { [key] = "x" });
        Assert.Throws<ArgumentException>(() => ModuleSettingsValidation.ValidatePublicSettings([definition]));
    }

    [Theory]
    [InlineData(DigitalBrainConfiguration.ModulesSectionName + ":X:Options")]
    [InlineData(DigitalBrainConfiguration.ModulesSectionName + ":X:Options:Endpoint")]
    [InlineData(DigitalBrainConfiguration.ModulesSectionName + ":X:Options:Bindings:a:0")]
    public void FlattenedOptionKeysArePublicSettings(string key)
        => ModuleSettingsValidation.ValidatePublicSettings(
            [new(new FakeModule(), new Dictionary<string, string?> { [key] = "x" })]);
}
