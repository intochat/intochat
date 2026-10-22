using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Sdk.Compute;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Sdk.Tests.Unit;

public sealed class ModulePriceListFacts
{
    [Fact]
    public void IndependentModuleListsComposeAsTheSameConcreteSingletons()
    {
        var services = new ServiceCollection();
        services.AddModulePriceList<FirstPriceList>("first", [new("first.read", "api", "call")]);
        services.AddModulePriceList<SecondPriceList>("second", [new("second.read", "api", "call")]);
        services.AddModulePriceList<FirstPriceList>("first", [new("first.read", "api", "call")]);
        using var provider = services.BuildServiceProvider();
        var lists = provider.GetServices<IPriceList>().ToArray();
        Assert.Equal(2, lists.Length);
        Assert.Same(provider.GetRequiredService<FirstPriceList>(), lists.OfType<FirstPriceList>().Single());
        Assert.Same(provider.GetRequiredService<SecondPriceList>(), lists.OfType<SecondPriceList>().Single());
        var registry = provider.GetRequiredService<ModulePriceListRegistry>();
        Assert.Equal("first", registry.Resolve("first.read").ModuleId);
        Assert.Equal(typeof(SecondPriceList), registry.Resolve("second.read").PriceListType);
        Assert.Equal("call", registry.GetOperation("first.read").Unit);
    }

    [Fact]
    public void ConflictingOwnershipFailsWhileComposingTheHost()
    {
        var services = new ServiceCollection();
        services.AddModulePriceList<FirstPriceList>("first", [new("shared.read", "api", "call")]);
        Assert.Throws<InvalidOperationException>(() => services.AddModulePriceList<SecondPriceList>("second", [new("shared.read", "api", "call")]));
        Assert.Throws<InvalidOperationException>(() => services.AddModulePriceList<FirstPriceList>("first", [new("shared.read", "api", "byte")]));
    }

    [Fact]
    public void CallerArraysCannotChangeRegisteredOwnership()
    {
        var services = new ServiceCollection();
        ExternalOperationDescriptor[] operations = [new("first.read", "api", "call")];
        services.AddModulePriceList<FirstPriceList>("first", operations);
        operations[0] = new("changed", "other", "byte");
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ModulePriceListRegistry>();
        var returned = registry.Resolve("first.read").Operations;
        returned[0] = operations[0];
        Assert.Equal("call", registry.GetOperation("first.read").Unit);
        Assert.Throws<InvalidOperationException>(() => registry.Resolve("changed"));
    }

    [Fact]
    public void NullDeclarationsFailWithAnArgumentError()
        => Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddModulePriceList<FirstPriceList>("first", [new("first.read", "api", "call"), null!]));

    [Theory]
    [InlineData(ServiceLifetime.Transient)]
    [InlineData(ServiceLifetime.Scoped)]
    public void ExistingNonSingletonPriceListsAreRejectedBeforeCompositionChanges(ServiceLifetime lifetime)
    {
        IServiceCollection services = new ServiceCollection();
        services.Add(new ServiceDescriptor(typeof(FirstPriceList), typeof(FirstPriceList), lifetime));
        var before = services.ToArray();
        Assert.Throws<InvalidOperationException>(() => services.AddModulePriceList<FirstPriceList>("first", [new("first.read", "api", "call")]));
        Assert.Equal(before, services.ToArray());
    }

    [Fact]
    public void AnIndependentEnumerableRegistrationCannotCreateASecondSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPriceList, FirstPriceList>();
        var before = services.ToArray();
        Assert.Throws<InvalidOperationException>(() => services.AddModulePriceList<FirstPriceList>("first", [new("first.read", "api", "call")]));
        Assert.Equal(before, services.ToArray());
    }

    [Fact]
    public void AConfiguredConcreteSingletonIsPreservedForBothResolutionPaths()
    {
        var services = new ServiceCollection();
        var configured = new FirstPriceList();
        services.AddSingleton(configured);
        services.AddModulePriceList<FirstPriceList>("first", [new("first.read", "api", "call")]);
        using var provider = services.BuildServiceProvider();
        Assert.Same(configured, provider.GetRequiredService<FirstPriceList>());
        Assert.Same(configured, Assert.Single(provider.GetServices<IPriceList>()));
    }

    [Fact]
    public void ModulesSharingAListTypeKeepOneAliasAndIdempotentOwnership()
    {
        var services = new ServiceCollection();
        services.AddModulePriceList<FirstPriceList>("first", [new("first.read", "api", "call")]);
        services.AddModulePriceList<FirstPriceList>("second", [new("second.read", "api", "call")]);
        services.AddModulePriceList<FirstPriceList>("second", [new("second.read", "api", "call")]);
        using var provider = services.BuildServiceProvider();
        var concrete = Assert.Single(provider.GetServices<FirstPriceList>());
        Assert.Same(concrete, Assert.Single(provider.GetServices<IPriceList>()));
        Assert.Equal(2, provider.GetServices<ModulePriceListRegistration>().Count());
        var registry = provider.GetRequiredService<ModulePriceListRegistry>();
        Assert.Equal("first", registry.Resolve("first.read").ModuleId);
        Assert.Equal("second", registry.Resolve("second.read").ModuleId);
    }

    [Fact]
    public void AConfiguredConcreteFactoryRunsOnceForBothResolutionPaths()
    {
        var services = new ServiceCollection();
        var configured = new FirstPriceList();
        var calls = 0;
        services.AddSingleton<FirstPriceList>(_ => { calls++; return configured; });
        services.AddModulePriceList<FirstPriceList>("first", [new("first.read", "api", "call")]);
        using var provider = services.BuildServiceProvider();
        Assert.Same(configured, Assert.Single(provider.GetServices<IPriceList>()));
        Assert.Same(configured, provider.GetRequiredService<FirstPriceList>());
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IndependentEnumerableInstancesAndTypedFactoriesAreRejectedBeforeCompositionChanges(bool factory)
    {
        var services = new ServiceCollection();
        if (factory) { services.AddSingleton<IPriceList, FirstPriceList>(_ => new FirstPriceList()); }
        else { services.AddSingleton<IPriceList>(new FirstPriceList()); }
        var before = services.ToArray();
        Assert.Throws<InvalidOperationException>(() => services.AddModulePriceList<FirstPriceList>("first", [new("first.read", "api", "call")]));
        Assert.Equal(before, services.ToArray());
    }

    [Theory]
    [InlineData("", "api", "call")]
    [InlineData("first.read", "", "call")]
    [InlineData("first.read", "api", "")]
    [InlineData("first.read", "api", " call ")]
    public void InvalidDeclarationsFailBeforeProviderResolution(string operation, string integration, string unit)
        => Assert.Throws<ArgumentException>(() => new ServiceCollection().AddModulePriceList<FirstPriceList>("first", [new(operation, integration, unit)]));

    private sealed class FirstPriceList : IPriceList
    {
        public long? MicrosPerUnit(CostKind kind, string item) => 0;
    }

    private sealed class SecondPriceList : IPriceList
    {
        public long? MicrosPerUnit(CostKind kind, string item) => null;
    }
}
