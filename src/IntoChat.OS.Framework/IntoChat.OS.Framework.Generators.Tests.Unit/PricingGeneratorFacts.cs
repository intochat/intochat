using System.Reflection;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Framework.Modules.Metering;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace IntoChat.OS.Framework.Generators.Tests.Unit;

public sealed class PricingGeneratorFacts
{
    private const string Header = """
        using IntoChat.OS.Framework.Modules;
        using IntoChat.OS.Framework.Modules;
        using IntoChat.OS.Connections.Compute;
        using IntoChat.OS.Contracts.Compute;
        using IntoChat.OS.Framework.Compute;
        using IntoChat.OS.Framework.Modules;
        namespace PricingFixture;
        public class PriceList : IPriceList
        {
            public long? MicrosPerUnit(CostKind kind, string item) => 0;
        }
        """;

    [Fact]
    public void GeneratedRegistrationContributesBothOperationsBeforeTheConfigureHook()
    {
        var source = Header + """

        [Module("priced")]
        [Integration("api", "Provider")]
        [ModulePriceList(typeof(PriceList))]
        [ExternalOperation("provider.read", "api", "request")]
        [ExternalOperation("provider.write", "api", "request")]
        public sealed partial class PricedModule : Module
        {
            partial void OnConfigure(IModuleServices services)
            {
                if (!System.Linq.Enumerable.Any(services.Services, descriptor => descriptor.ServiceType == typeof(IntoChat.OS.Framework.Modules.Metering.ModulePriceListRegistration)))
                    throw new System.InvalidOperationException("Pricing is unavailable during configuration.");
            }
        }
        """;
        var driver = GeneratorHarness.CreateDriver().RunGeneratorsAndUpdateCompilation(GeneratorHarness.Compile(source), out var output, out var diagnostics, TestContext.Current.CancellationToken);
        GeneratorHarness.AssertNoErrors(output, diagnostics);
        using var bytes = new MemoryStream();
        var emitted = output.Emit(bytes, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var assembly = Assembly.Load(bytes.ToArray());
        var module = (IntoChat.OS.Framework.Modules.Module)Activator.CreateInstance(assembly.GetType("PricingFixture.PricedModule")!)!;
        var services = new ServiceCollection();
        module.ConfigureServices(services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var registration = Assert.Single(provider.GetServices<ModulePriceListRegistration>());
        Assert.Equal("priced", registration.ModuleId);
        Assert.Equal<ExternalOperationDescriptor>([new("provider.read", "api", "request"), new("provider.write", "api", "request")], registration.Operations);
        Assert.Single(provider.GetServices<IPriceList>());
        var again = GeneratorHarness.Render(GeneratorHarness.CreateDriver().RunGenerators(GeneratorHarness.Compile(source), TestContext.Current.CancellationToken));
        Assert.Equal(GeneratorHarness.Render(driver), again);
    }

    [Theory]
    [InlineData("typeof(string)")]
    [InlineData("typeof(AbstractList)")]
    [InlineData("typeof(GenericList<>)")]
    [InlineData("typeof(Hidden.List)")]
    public void InvalidPriceTypesReportIOS0005(string type)
        => Diagnostic("IOS0005", $$"""
        public abstract class AbstractList : PriceList;
        public class GenericList<T> : PriceList;
        public class Hidden { private class List : PriceList; }
        [Module("priced")]
        [ModulePriceList({{type}})]
        public sealed partial class PricedModule : Module;
        """);

    [Fact]
    public void DuplicatePriceListsReportIOS0006()
        => Diagnostic("IOS0006", """
        [Module("priced")]
        [ModulePriceList(typeof(PriceList))]
        [ModulePriceList(typeof(PriceList))]
        public sealed partial class PricedModule : Module;
        """);

    [Theory]
    [InlineData("", "api", "request")]
    [InlineData("provider.read", "unknown", "request")]
    [InlineData("provider.read", "api", "")]
    [InlineData("provider.read", "api", "per request")]
    public void InvalidOperationsReportIOS0007(string name, string integration, string unit)
        => Diagnostic("IOS0007", $$"""
        [Module("priced")]
        [Integration("api", "Provider")]
        [ModulePriceList(typeof(PriceList))]
        [ExternalOperation("{{name}}", "{{integration}}", "{{unit}}")]
        public sealed partial class PricedModule : Module;
        """);

    [Fact]
    public void AnOperationWithoutAPriceListReportsIOS0007()
        => Diagnostic("IOS0007", """
        [Module("priced")]
        [Integration("api", "Provider")]
        [ExternalOperation("provider.read", "api", "request")]
        public sealed partial class PricedModule : Module;
        """);

    // A module that bills against another module's integration names it with [UsesIntegration]; no schema is emitted for it.
    [Fact]
    public void AUsedIntegrationOwnsOperationsWithoutDeclaringTheIntegration()
    {
        var source = Header + """

        [Module("borrower")]
        [UsesIntegration("api")]
        [ModulePriceList(typeof(PriceList))]
        [ExternalOperation("provider.read", "api", "request")]
        public sealed partial class BorrowerModule : Module;
        """;
        var result = GeneratorHarness.CreateDriver().RunGenerators(GeneratorHarness.Compile(source), TestContext.Current.CancellationToken).GetRunResult();
        Assert.Empty(result.Diagnostics);
        var generated = Assert.Single(result.Results.Single().GeneratedSources).SourceText.ToString();
        Assert.Contains("\"provider.read\", \"api\", \"request\"", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("Integration { get; }", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateOperationsReportIOS0008EvenWithDifferentUnits()
        => Diagnostic("IOS0008", """
        [Module("priced")]
        [Integration("api", "Provider")]
        [ModulePriceList(typeof(PriceList))]
        [ExternalOperation("provider.read", "api", "request")]
        [ExternalOperation("provider.read", "api", "page")]
        public sealed partial class PricedModule : Module;
        """);

    [Fact]
    public void SameCompilationOwnershipConflictsReportBothModulesAndKeepUnrelatedOutput()
    {
        var source = Header + """

        [Module("first"), Integration("api", "Provider"), ModulePriceList(typeof(PriceList)), ExternalOperation("provider.read", "api", "request")]
        public sealed partial class FirstModule : Module;
        [Module("second"), Integration("api", "Provider"), ModulePriceList(typeof(PriceList)), ExternalOperation("provider.read", "api", "page")]
        public sealed partial class SecondModule : Module;
        [Module("other")]
        public sealed partial class OtherModule : Module;
        """;
        var result = GeneratorHarness.CreateDriver().RunGenerators(GeneratorHarness.Compile(source), TestContext.Current.CancellationToken).GetRunResult();
        Assert.Equal(2, result.Diagnostics.Count(diagnostic => diagnostic.Id == "IOS0009"));
        var generated = Assert.Single(result.Results.Single().GeneratedSources);
        Assert.Contains("OtherModule", generated.HintName, StringComparison.Ordinal);
    }

    private static void Diagnostic(string id, string body)
    {
        var result = GeneratorHarness.CreateDriver().RunGenerators(GeneratorHarness.Compile(Header + "\n" + body), TestContext.Current.CancellationToken).GetRunResult();
        Assert.Single(result.Diagnostics, diagnostic => diagnostic.Id == id && diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Empty(result.GeneratedTrees);
    }
}
