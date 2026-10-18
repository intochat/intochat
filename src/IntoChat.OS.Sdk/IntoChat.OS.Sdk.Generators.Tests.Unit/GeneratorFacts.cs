using Microsoft.CodeAnalysis;

namespace IntoChat.OS.Sdk.Generators.Tests.Unit;

public sealed class GeneratorFacts
{
    private const string Header = """
        using IntoChat.OS.Framework.Modules;
        using IntoChat.OS.Sdk;
        namespace Fixture;
        """;

    private static string Source(string body) => Header + "\n" + body;

    private static readonly string[] OrleansMarkers = ["GenerateSerializer", "Alias(", "IGrainWith", "[Id("];

    private static void Verify(string name, string body)
    {
        var compilation = GeneratorHarness.Compile(Source(body));
        var driver = GeneratorHarness.CreateDriver().RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics, TestContext.Current.CancellationToken);
        GeneratorHarness.AssertNoErrors(output, diagnostics);
        var text = GeneratorHarness.Render(driver);

        // A second, independent run over the same input must produce identical bytes.
        var again = GeneratorHarness.Render(GeneratorHarness.CreateDriver().RunGenerators(GeneratorHarness.Compile(Source(body)), TestContext.Current.CancellationToken));
        Assert.Equal(text, again);
        GeneratorHarness.AssertGolden(Path.Combine(GeneratorHarness.GoldenDirectory(), name + ".g.cs"), text);
    }

    [Fact]
    public void SampleModule() => Verify("SampleModule", """
        [Module("sample")]
        [Integration("sample", "Sample")]
        [Secret("ApiKey")]
        public sealed partial class SampleModule : Module<SampleOptions>
        {
            partial void OnConfigure(IModuleServices services) => services.Services.ToString();
        }

        public sealed class SampleOptions : IModuleOptions
        {
            [Setting] public string Region { get; init; } = "eu";
            public void Validate() { }
        }
        """);

    [Fact]
    public void Minimal() => Verify("Minimal", """
        [Module("minimal")]
        public sealed partial class MinimalModule : Module;
        """);

    [Fact]
    public void OptionsWithSettings() => Verify("OptionsWithSettings", """
        [Module("options")]
        [Integration("options", "Options")]
        [Setting("Region")]
        [Setting("Mode", Optional = true)]
        public sealed partial class OptionsModule : Module<OptionsModuleOptions>;

        public sealed class OptionsModuleOptions : IModuleOptions
        {
            [Setting] public string Region { get; init; } = "eu";
            public void Validate() { }
        }
        """);

    [Fact]
    public void SecretsWithOptional() => Verify("SecretsWithOptional", """
        [Module("vendor")]
        [Integration("vendor", "Vendor \"Cloud\"")]
        [Secret("ApiKey")]
        [Secret("Token", Optional = true)]
        [Setting("Endpoint")]
        public sealed partial class VendorModule : Module;
        """);

    [Fact]
    public void TwoIntegrations() => Verify("TwoIntegrations", """
        [Module("gmail")]
        [Integration("google-oauth", "Google OAuth")]
        [Integration("google-pubsub", "Google Pub/Sub push")]
        [Secret("ClientId", Integration = "google-oauth")]
        [Secret("ClientSecret", Integration = "google-oauth")]
        [Setting("Audience", Integration = "google-pubsub")]
        [Setting("ServiceAccountEmail", Integration = "google-pubsub")]
        public sealed partial class MailModule : Module;
        """);

    [Fact]
    public void HttpModule() => Verify("HttpModule", """
        [Module("web")]
        public sealed partial class WebModule : Module, IntoChat.OS.Framework.IHttpModule
        {
            public void Configure(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints) { }
        }
        """);

    [Fact]
    public void RequiresLicense() => Verify("RequiresLicense", """
        public interface IContract;

        [Module("licensed")]
        [ModuleContracts(typeof(IContract))]
        [RequiresLicense("pro")]
        public sealed partial class LicensedModule : Module
        {
            partial void OnConfigure(IModuleServices services) => services.Services.ToString();
        }
        """);

    [Fact]
    public void Hosting() => Verify("Hosting", """
        [Module("hosted")]
        public sealed partial class HostedModule : Module;

        [ModuleHosting(typeof(HostedModule))]
        public sealed partial class HostedModuleHosting;
        """);

    [Fact]
    public void ANonPartialModuleReportsIOS0001AtItsName() => Diagnostic("IOS0001", """
        [Module("a")]
        public sealed class NotPartialModule : Module;
        """);

    [Fact]
    public void AnInvalidModuleIdReportsIOS0002() => Diagnostic("IOS0002", """
        [Module("bad id")]
        public sealed partial class BadIdModule : Module;
        """);

    [Fact]
    public void AnInvalidFieldNameReportsIOS0002() => Diagnostic("IOS0002", """
        [Module("a")]
        [Integration("a", "A")]
        [Secret("1bad")]
        public sealed partial class BadFieldModule : Module;
        """);

    [Fact]
    public void ASecretWithoutAnIntegrationAmongManyReportsIOS0003() => Diagnostic("IOS0003", """
        [Module("a")]
        [Integration("one", "One")]
        [Integration("two", "Two")]
        [Secret("Key")]
        public sealed partial class AmbiguousModule : Module;
        """);

    [Fact]
    public void ModuleOnANonModuleTypeReportsIOS0004() => Diagnostic("IOS0004", """
        [Module("a")]
        public sealed partial class PlainClass;
        """);

    [Fact]
    public void HostingNamingANonModuleReportsIOS0004() => Diagnostic("IOS0004", """
        public sealed class NotAModule;

        [ModuleHosting(typeof(NotAModule))]
        public sealed partial class WrongHosting;
        """);

    [Fact]
    public void EveryGoldenCompilesAndIsPresent()
        => Assert.True(Directory.GetFiles(GeneratorHarness.GoldenDirectory(), "*.g.cs").Length >= 8);

    [Fact]
    public void GeneratedSourceContainsNoOrleansTypes()
    {
        var goldens = Directory.GetFiles(GeneratorHarness.GoldenDirectory(), "*.g.cs");
        Assert.NotEmpty(goldens);
        foreach (var golden in goldens)
        {
            var text = File.ReadAllText(golden);
            foreach (var marker in OrleansMarkers)
            {
                Assert.False(text.Contains(marker, StringComparison.Ordinal), $"{Path.GetFileName(golden)} contains '{marker}'");
            }
        }
    }

    private static void Diagnostic(string id, string body)
    {
        var source = Source(body);
        var compilation = GeneratorHarness.Compile(source);
        var result = GeneratorHarness.CreateDriver().RunGenerators(compilation, TestContext.Current.CancellationToken).GetRunResult();
        var reported = Assert.Single(result.Diagnostics, d => d.Id == id);
        Assert.Equal(DiagnosticSeverity.Error, reported.Severity);
        Assert.Empty(result.GeneratedTrees.Where(generated => generated.FilePath.Contains("Module", StringComparison.Ordinal)));
        // The location is the class name on the declaration line.
        var line = source.Split('\n').Select((text, index) => (text, index)).Last(pair => pair.text.Contains("class ", StringComparison.Ordinal)).index;
        Assert.Equal(line, reported.Location.GetLineSpan().StartLinePosition.Line);
    }
}
