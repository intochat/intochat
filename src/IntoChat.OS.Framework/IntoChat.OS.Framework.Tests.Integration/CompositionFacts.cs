using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Microsoft.Orleans;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace IntoChat.OS.Framework.Tests.Integration;

public sealed class CompositionFacts(FrameworkHost host)
{
    [Trait("host", "own")]
    [Fact]
    public async Task HostInventoryListsTheSelectedModulesInDependencyOrder()
    {
        await using var brain = await TestBrain.Create().WithModule<Dependency>().WithModule<Dependent, DependentOptions>()
            .StartAsync(TestContext.Current.CancellationToken);

        // The in-process host composes the orleans module first: it owns the cluster's external systems.
        Assert.Equal([typeof(OrleansModule), typeof(Dependency), typeof(Dependent)],
            brain.Services.GetRequiredService<ModuleInventory>().Types);
    }

    [Fact]
    public async Task EverySiloPrefersTheLatestCompatibleVersion()
    {
        var brain = host.Brain;

        var versioning = brain.Services.GetRequiredService<IOptions<GrainVersioningOptions>>().Value;

        Assert.Equal(nameof(BackwardCompatible), versioning.DefaultCompatibilityStrategy);
        Assert.Equal(nameof(LatestVersion), versioning.DefaultVersionSelectorStrategy);
    }

    [Fact]
    public void DefinitionCopiesConfigurationAndDeduplicatesIdenticalModules()
    {
        var values = new Dictionary<string, string?> { ["Feature:Enabled"] = "false" };
        var first = new ModuleDefinition(new ExampleModule(), values);
        values["Feature:Enabled"] = "true";
        Assert.Equal("false", first.Settings["Feature:Enabled"]);
        Assert.Single(ModuleComposition.Resolve([first, new(new ExampleModule(), new Dictionary<string, string?> { ["Feature:Enabled"] = "false" })]));
    }

    [Fact]
    public void ConflictingDefinitionsFail()
    {
        var first = new ModuleDefinition(new ExampleModule(), new Dictionary<string, string?> { ["Value"] = "1" });
        var second = new ModuleDefinition(new ExampleModule(), new Dictionary<string, string?> { ["Value"] = "2" });
        Assert.Throws<InvalidOperationException>(() => ModuleComposition.Resolve([first, second]));
    }

    [Fact]
    public void DistinctModulesCannotDisagreeOnSharedSettings()
    {
        var first = new ModuleDefinition(new Dependency(), new Dictionary<string, string?> { ["Shared:Value"] = "first" });
        var second = new ModuleDefinition(new Dependent(), new Dictionary<string, string?> { ["Shared:Value"] = "second" });
        Assert.Throws<InvalidOperationException>(() => ModuleComposition.Resolve([first, second]));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TransitiveDependencyRegistersExactlyOnce()
    {
        await using var brain = await TestBrain.Create().WithModule<Dependency>().WithModule<Dependent>()
            .StartAsync(TestContext.Current.CancellationToken);

        Assert.Single(brain.Services.GetServices<Marker>());
    }

    [Fact]
    public void BuildSnapshotsOptionsByValueSoLaterMutationOfACapturedInstanceChangesNothing()
    {
        ExampleOptions? captured = null;
        var draft = new BrainCompositionBuilder().WithModule<ExampleModule, ExampleOptions>(
            o => { captured = o; o.Endpoint = "chosen"; });
        var snapshot = draft.Build();
        captured!.Endpoint = "mutated";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Assert.Single(snapshot.Modules).Settings).Build();
        Assert.Equal("chosen", configuration.GetModuleOptions<ExampleOptions>(nameof(ExampleModule)).Endpoint);
    }

    [Fact]
    public void DuplicateDeclarationAndMissingModuleFail()
    {
        var draft = new BrainCompositionBuilder().WithModule<ExampleModule, ExampleOptions>();
        Assert.Throws<InvalidOperationException>(() => draft.WithModule<ExampleModule, ExampleOptions>());
        Assert.Throws<InvalidOperationException>(() => draft.ConfigureModule<OtherModule>(_ => { }));
        Assert.Throws<InvalidOperationException>(() => draft.ConfigureModule<Dependent, DependentOptions>(_ => { }));
    }

    [Fact]
    public void ConfigureModuleEditsTheDeclaredOptionsAndKeepsTheRest()
    {
        var draft = new BrainCompositionBuilder().WithModule<ExampleModule, ExampleOptions>(o => { o.Endpoint = "first"; o.Delay = 5; });
        draft.ConfigureModule<ExampleModule, ExampleOptions>(o => o.Endpoint = null);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Assert.Single(draft.Build().Modules).Settings).Build();
        var options = configuration.GetModuleOptions<ExampleOptions>(nameof(ExampleModule));
        Assert.Null(options.Endpoint);
        Assert.Equal(5, options.Delay);
        Assert.Equal("keep", options.Name);
    }

    [Fact]
    public void OptionsCompileToConfigurationKeysSoAHostOverridesThemByPrecedence()
    {
        var application = new BrainCompositionBuilder().WithModule<ExampleModule, ExampleOptions>(
            o => { o.Endpoint = "old"; o.Enabled = true; o.Delay = 250; });
        // A later configuration source (a host's env, args, or files) wins over the compiled defaults.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Assert.Single(application.Build().Modules).Settings)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [IntoChatOSConfiguration.ModuleOptionsSection(nameof(ExampleModule)) + ":Endpoint"] = null,
                [IntoChatOSConfiguration.ModuleOptionsSection(nameof(ExampleModule)) + ":Enabled"] = "false",
            }).Build();
        var applied = configuration.GetModuleOptions<ExampleOptions>(nameof(ExampleModule));
        Assert.Null(applied.Endpoint);
        Assert.False(applied.Enabled);
        Assert.Equal(250, applied.Delay);
    }

    [Fact]
    public void SettingsFreeModulesNeedNoOptions()
        => Assert.Equal(typeof(OtherModule), Assert.Single(new BrainCompositionBuilder().WithModule<OtherModule>().Build().Modules).ModuleType);

    [Fact]
    public void OptionsValidationRunsAtBuild()
    {
        var draft = new BrainCompositionBuilder().WithModule<ExampleModule, ExampleOptions>(o => o.Delay = -1);
        Assert.Throws<ArgumentOutOfRangeException>(draft.Build);
    }

    [Theory]
    [InlineData("Orleans:ClusterId")]
    [InlineData("ConnectionStrings:storage")]
    [InlineData(ModuleSettingsValidation.TestingSectionPrefix + "AnyKey")]
    [InlineData(IntoChatOSConfiguration.ModulesSectionName + ":0")]
    [InlineData("AI:OpenAI:ApiKey")]
    [InlineData("Provider:PrivateKeyPem")]
    public void HarnessAndCredentialKeysCannotBePublicSettings(string key)
        => Assert.Throws<ArgumentException>(() => ModuleSettingsValidation.ValidatePublicSettings(
            [new(new ExampleModule(), new Dictionary<string, string?> { [key] = "override" })]));

    public sealed class Marker;
    public sealed class OtherModule() : Module(new(nameof(OtherModule))) { public override void Configure(ISiloBuilder silo) { } }
    public sealed class ExampleModule() : Module<ExampleOptions>(new(nameof(ExampleModule))) { public override void Configure(ISiloBuilder silo) { } }
    public sealed class ExampleOptions : IModuleOptions
    {
        public string? Endpoint { get; set; }
        public bool Enabled { get; set; }
        public int Delay { get; set; }
        public string Name { get; set; } = "keep";
        public void Validate() => ArgumentOutOfRangeException.ThrowIfNegative(Delay);
    }
    public sealed class Dependency() : Module(new(nameof(Dependency)))
    {
        public override void Configure(ISiloBuilder silo) => silo.Services.AddSingleton<Marker>();
    }
    public sealed class Dependent() : Module<DependentOptions>(new(nameof(Dependent)))
    {
        public override void Configure(ISiloBuilder silo)
        {
            if (!silo.Services.Any(s => s.ServiceType == typeof(Marker)))
            { throw new InvalidOperationException("Dependency was not configured before dependent."); }
        }
    }
    public sealed class DependentOptions : IModuleOptions { public void Validate() { } }
}
