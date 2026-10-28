using DigitalBrain;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Spi;
using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Microsoft.CSharp.Authoring;
using IntoChat.OS.Modules.Microsoft.CSharp.Catalog;
using IntoChat.OS.Modules.Microsoft.CSharp.Files;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Unit;

public sealed class CSharpFacts
{
    private static BrainType Neuron(string provider, string name) => new(name, provider + "." + name, DigitalBrain.TypeKind.Neuron, provider, "does " + name, ["Task Run()"], []);

    private static BrainType Signal(string provider, string name) => new(name, provider + "." + name, DigitalBrain.TypeKind.Signal, provider, "", ["int Count"], []);

    private static CSharpContractDiscovery Discovery(params BrainType[] types) => new(new FakeKernel(types),
        new ModuleInventory(types.Select(type => type.Provider).Distinct().Select(id => OsModule.WithContracts(id, typeof(IDiscoveryFixture).Assembly))));

    private static BrainType Entry<T>(string provider = "fixture") => new(typeof(T).FullName!, "fixture", DigitalBrain.TypeKind.Neuron, provider, "Fixture", [], []);

    [Fact]
    public async Task WithNoModulesSelectedTheCatalogListsInstalledModuleIdsInOrderAndNoContracts()
    {
        var snapshot = await Discovery(Neuron("time", "Timer"), Neuron("ai", "Chat"), Signal("time", "Tick")).Read([], TestContext.Current.CancellationToken);

        Assert.Equal(["ai", "time"], snapshot.Modules.Select(module => module.Id));
        Assert.Empty(snapshot.Contracts);
        Assert.Equal(CSharpContractDiscovery.Example, snapshot.Example);
        Assert.False(snapshot.Truncated);
    }

    [Fact]
    public async Task SelectingAModuleIncludesConstructibleRequestAndResultDeclarations()
    {
        var snapshot = await Discovery(Entry<IDiscoveryFixture>(), Entry<IHiddenFixture>("hidden"))
            .Read(["FIXTURE"], TestContext.Current.CancellationToken);
        var declarations = string.Join("\n", snapshot.Contracts);
        Assert.Contains("Read(", declarations, StringComparison.Ordinal);
        Assert.Contains("DiscoveryRequest(", declarations, StringComparison.Ordinal);
        Assert.Contains("string? Cursor", declarations, StringComparison.Ordinal);
        Assert.Contains("DiscoveryAddress[] Recipients", declarations, StringComparison.Ordinal);
        Assert.Contains("{ get; init; }", declarations, StringComparison.Ordinal);
        Assert.Contains("DiscoveryMode", declarations, StringComparison.Ordinal);
        Assert.Contains("Preview", declarations, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<", declarations, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(IHiddenFixture), declarations, StringComparison.Ordinal);
        Assert.False(snapshot.Truncated);
    }

    [Fact]
    public async Task ConnectorsAreNotListedAsContracts()
    {
        var connector = new BrainType("Gmail", "gmail", DigitalBrain.TypeKind.Connector, "gmail", "", [], []);

        var snapshot = await Discovery(Neuron("time", "Timer"), connector).Read([], TestContext.Current.CancellationToken);

        Assert.Equal(["time"], snapshot.Modules.Select(module => module.Id));
    }

    [Fact]
    public async Task AnUnknownModuleIdIsRefusedNamingTheInstalledOnes()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Discovery(Neuron("time", "Timer")).Read(["nope"], TestContext.Current.CancellationToken));

        Assert.Contains("nope", error.Message, StringComparison.Ordinal);
        Assert.Contains("time", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOversizedCatalogOmitsWholeMethodClosuresAndNamesTheDetailSelectors()
    {
        var entries = Enumerable.Range(0, 25).Select(index => Entry<IDiscoveryFixture>("fixture-" + index)).ToArray();
        var discovery = Discovery(entries);
        var snapshot = await discovery.Read(entries.Select(entry => entry.Provider).ToArray(), TestContext.Current.CancellationToken);

        Assert.True(snapshot.Truncated);
        Assert.Contains(snapshot.Contracts, contract => contract.Contains("omitted", StringComparison.OrdinalIgnoreCase)
            && contract.Contains(nameof(IDiscoveryFixture), StringComparison.Ordinal));
        foreach (var contract in snapshot.Contracts.Where(contract => contract.Contains("Read(", StringComparison.Ordinal)))
        { Assert.Contains("DiscoveryRequest(", contract, StringComparison.Ordinal); }
        Assert.True(System.Text.Json.JsonSerializer.Serialize(snapshot).Length <= 24000);
        var detail = await discovery.ReadDetail(entries[^1].Provider, typeof(IDiscoveryFixture).FullName!, "Read", TestContext.Current.CancellationToken);
        Assert.False(detail.Truncated);
        Assert.Contains("DiscoveryRequest(", Assert.Single(detail.Contracts), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetailSelectsOneMethodIncludesInheritedMethodsAndTerminatesCyclicShapes()
    {
        var discovery = Discovery(Entry<IDiscoveryFixture>());
        var detail = await discovery.ReadDetail("fixture", typeof(IDiscoveryFixture).FullName!, "Read", TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Inherited(", Assert.Single(detail.Contracts), StringComparison.Ordinal);
        Assert.Contains("DiscoveryNode? Next", detail.Contracts[0], StringComparison.Ordinal);
        var inherited = await discovery.ReadDetail("fixture", typeof(IDiscoveryFixture).FullName!, "Inherited", TestContext.Current.CancellationToken);
        Assert.Contains("Inherited(", Assert.Single(inherited.Contracts), StringComparison.Ordinal);
        Assert.Equal(detail, detail with { Contracts = detail.Contracts });
        Assert.Equal(detail.Contracts, (await discovery.ReadDetail("fixture", typeof(IDiscoveryFixture).FullName!, "Read", TestContext.Current.CancellationToken)).Contracts);
    }

    [Fact]
    public async Task ExplicitPolymorphicAlternativesAndDiscriminatorsAreDiscoverable()
    {
        var detail = await Discovery(Entry<IDiscoveryFixture>()).ReadDetail("fixture", typeof(IDiscoveryFixture).FullName!, "Choose", TestContext.Current.CancellationToken);
        var declaration = Assert.Single(detail.Contracts);
        Assert.Contains("$kind", declaration, StringComparison.Ordinal);
        Assert.Contains("email", declaration, StringComparison.Ordinal);
        Assert.Contains("DiscoveryEmail(", declaration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InheritedToolSourceAndCoreVocabularyKeepTheirCompletePublicShapes()
    {
        var discovery = Discovery(Entry<IDiscoveryFixture>());
        var tools = await discovery.ReadDetail("fixture", typeof(IDiscoveryFixture).FullName!, "Tools", TestContext.Current.CancellationToken);
        Assert.Contains("ToolSet(", Assert.Single(tools.Contracts), StringComparison.Ordinal);
        Assert.Contains("ToolDescriptor(", tools.Contracts[0], StringComparison.Ordinal);
        var check = await discovery.ReadDetail("fixture", typeof(IDiscoveryFixture).FullName!, "Check", TestContext.Current.CancellationToken);
        Assert.Contains("IntoChat.OS.Verdict", Assert.Single(check.Contracts), StringComparison.Ordinal);
        Assert.Contains("CancellationToken cancellationToken = default", check.Contracts[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedGenericTypesAndEnumDefaultsDescribeCallableCSharpTypes()
    {
        var detail = await Discovery(Entry<IDiscoveryFixture>()).ReadDetail("fixture", typeof(IDiscoveryFixture).FullName!, "Nested", TestContext.Current.CancellationToken);
        var schema = Assert.Single(detail.Contracts);
        Assert.Contains("DiscoveryOuter<string>.Nested<int?>", schema, StringComparison.Ordinal);
        Assert.Contains("System.Uri source", schema, StringComparison.Ordinal);
        Assert.Contains("DiscoveryMode)0", schema, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    public async Task OptionalLiteralDefaultsCompileRegardlessOfTheDiscoveryCulture(string culture)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
            var detail = await Discovery(Entry<IDiscoveryFixture>()).ReadDetail("fixture", typeof(IDiscoveryFixture).FullName!, "Defaults", TestContext.Current.CancellationToken);
            var schema = Assert.Single(detail.Contracts);
            var source = "public interface Defaults" + schema[schema.IndexOf(" {", StringComparison.Ordinal)..];
            var compilation = global::Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Defaults",
                [global::Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)],
                [global::Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                new(global::Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
            Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity == global::Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
            Assert.Contains("float fraction = 0.5f", schema, StringComparison.Ordinal);
            Assert.Contains("decimal amount = 0.5m", schema, StringComparison.Ordinal);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task CoreContractsUseTheSameProviderIdentityAsTheKernel()
    {
        var entry = Entry<IBrain>(typeof(IBrain).Assembly.GetName().Name!);
        var discovery = new CSharpContractDiscovery(new FakeKernel([entry]), new ModuleInventory([]));
        var detail = await discovery.ReadDetail(entry.Provider, entry.Name, "Read", TestContext.Current.CancellationToken);
        Assert.Contains("Read(", Assert.Single(detail.Contracts), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InheritedPlainCapabilitiesRemainCallableLikeTheKernelCatalog()
    {
        var detail = await Discovery(Entry<IDiscoveryFixture>()).ReadDetail("fixture", typeof(IDiscoveryFixture).FullName!, "Capability", TestContext.Current.CancellationToken);
        Assert.Contains("Capability(", Assert.Single(detail.Contracts), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetailRejectsUninstalledRootsUnknownMethodsAndUnresolvableNames()
    {
        var discovery = Discovery(Entry<IDiscoveryFixture>(), Neuron("fixture", "Missing"));
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<ArgumentException>(() => discovery.ReadDetail("hidden", typeof(IHiddenFixture).FullName!, null, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => discovery.ReadDetail("fixture", typeof(IHiddenFixture).FullName!, null, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => discovery.ReadDetail("fixture", typeof(IDiscoveryFixture).FullName!, "Missing", ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => discovery.ReadDetail("fixture", "Missing", null, ct));
    }

    [Fact]
    public async Task ASingleUnboundedSchemaFailsClearlyInsteadOfReturningAPartialDeclaration()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Discovery(Entry<IExpandingFixture>())
            .ReadDetail("fixture", typeof(IExpandingFixture).FullName!, "Read", TestContext.Current.CancellationToken));
        Assert.Contains("schema exceeds", error.Message, StringComparison.Ordinal);
        Assert.InRange(error.Message.Length, 1, 500);
    }

    [Fact]
    public async Task MultipleExplicitAnchorsCannotSilentlyResolveTheSameContractName()
    {
        var other = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new("DiscoveryCollision"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
        other.DefineDynamicModule("Contracts").DefineType(typeof(IDiscoveryFixture).FullName!, System.Reflection.TypeAttributes.Public).CreateType();
        var discovery = new CSharpContractDiscovery(new FakeKernel([Entry<IDiscoveryFixture>()]),
            new ModuleInventory([OsModule.WithContracts("fixture", typeof(IDiscoveryFixture).Assembly), OsModule.WithContracts("fixture", other)]));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => discovery.ReadDetail("fixture", typeof(IDiscoveryFixture).FullName!, "Read", TestContext.Current.CancellationToken));
        Assert.Contains("2 public resolutions", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANullModuleListIsRefused()
        => await Assert.ThrowsAsync<ArgumentNullException>(() => Discovery().Read(null!, TestContext.Current.CancellationToken));

    [Fact]
    public void AFileViewCarriesTheFilesShortIdNamePurposeAndCreationTime()
    {
        var created = new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);
        var file = new CSharpFileSnapshot("long-id", "source", new Dictionary<string, string>(), null, "ready", null, true, "Tea timer", "Brews tea", created);

        var view = CSharpFileView.Of("t1", file, "log line");

        Assert.Equal(new CSharpDescription("t1", "Tea timer", "Brews tea", created), view.Description);
        Assert.Same(file, view.File);
        Assert.Equal("log line", view.Logs);
    }

    [Fact]
    public void AFileWithNoCreationTimeIsDescribedWithTheDefault()
    {
        var file = new CSharpFileSnapshot("id", "", new Dictionary<string, string>(), null, "new", null, false, "n", "p", null);

        Assert.Equal(default, CSharpFileView.Of("x", file).Description.CreatedAt);
        Assert.Null(CSharpFileView.Of("x", file).Logs);
    }

    private sealed class FakeKernel(BrainType[] types) : IBrainObserver
    {
        public IAsyncEnumerable<BrainInstance> Instances<T>(CancellationToken cancellationToken) where T : class => throw new NotSupportedException();
        public IAsyncEnumerable<BrainInstance> Instances(string? type, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrainType[]> Types(CancellationToken cancellationToken = default) => Task.FromResult(types);
        public Task<string[]> Brains(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ObservedSynapse[]> Synapses(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<SignalDelivery> Deliveries(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
