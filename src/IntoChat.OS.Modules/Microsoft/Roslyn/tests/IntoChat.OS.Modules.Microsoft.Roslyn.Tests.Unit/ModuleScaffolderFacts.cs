using DigitalBrain.Testing;
using IntoChat.OS.Modules.Microsoft.Roslyn.Contracts;

namespace IntoChat.OS.Modules.Microsoft.Roslyn.Tests;

public sealed class ModuleScaffolderFacts
{
    private const string SourceFolder = "s" + "rc";

    [Fact]
    public async Task ScaffoldsAModuleInATempDirectoryWhoseManifestAndReadmePassValidation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "into-scaffold-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var scaffolder = new ModuleScaffolderNeuron(tempDir);
            var request = new ScaffoldModuleRequest(
                Category: "Sample",
                Name: "Widget",
                Secrets: ["ApiKey", "SigningToken"],
                Actions: ["Execute", "Status"]);

            var result = await scaffolder.Scaffold(request, TestContext.Current.CancellationToken);

            Assert.NotNull(result);
            Assert.True(Directory.Exists(result.ModuleDirectory));
            Assert.NotEmpty(result.CreatedFiles);

            var contractsCsproj = Path.Combine(result.ModuleDirectory, "Contracts", "IntoChat.OS.Modules.Sample.Widget.Contracts", "IntoChat.OS.Modules.Sample.Widget.Contracts.csproj");
            var contractsNeuron = Path.Combine(result.ModuleDirectory, "Contracts", "IntoChat.OS.Modules.Sample.Widget.Contracts", "IWidgetNeuron.cs");
            var srcCsproj = Path.Combine(result.ModuleDirectory, SourceFolder, "IntoChat.OS.Modules.Sample.Widget", "IntoChat.OS.Modules.Sample.Widget.csproj");
            var moduleClass = Path.Combine(result.ModuleDirectory, SourceFolder, "IntoChat.OS.Modules.Sample.Widget", "WidgetModule.cs");
            var neuronClass = Path.Combine(result.ModuleDirectory, SourceFolder, "IntoChat.OS.Modules.Sample.Widget", "WidgetNeuron.cs");
            var optionsClass = Path.Combine(result.ModuleDirectory, SourceFolder, "IntoChat.OS.Modules.Sample.Widget", "WidgetOptions.cs");
            var testsCsproj = Path.Combine(result.ModuleDirectory, "tests", "IntoChat.OS.Modules.Sample.Widget.Tests.Unit", "IntoChat.OS.Modules.Sample.Widget.Tests.Unit.csproj");
            var testsFacts = Path.Combine(result.ModuleDirectory, "tests", "IntoChat.OS.Modules.Sample.Widget.Tests.Unit", "WidgetModuleFacts.cs");
            var readme = Path.Combine(result.ModuleDirectory, "README.md");

            Assert.True(File.Exists(contractsCsproj), $"{contractsCsproj} should exist");
            Assert.True(File.Exists(contractsNeuron), $"{contractsNeuron} should exist");
            Assert.True(File.Exists(srcCsproj), $"{srcCsproj} should exist");
            Assert.True(File.Exists(moduleClass), $"{moduleClass} should exist");
            Assert.True(File.Exists(neuronClass), $"{neuronClass} should exist");
            Assert.True(File.Exists(optionsClass), $"{optionsClass} should exist");
            Assert.True(File.Exists(testsCsproj), $"{testsCsproj} should exist");
            Assert.True(File.Exists(testsFacts), $"{testsFacts} should exist");
            Assert.True(File.Exists(readme), $"{readme} should exist");

            // Validate README content
            var readmeContent = File.ReadAllText(readme);
            Assert.Contains("# IntoChat.OS.Modules.Sample.Widget", readmeContent);
            Assert.Contains("Module id: `sample.widget`.", readmeContent);
            Assert.Contains("ApiKey", readmeContent);
            Assert.Contains("SigningToken", readmeContent);
            Assert.Contains("Contracts: assembly `IntoChat.OS.Modules.Sample.Widget.Contracts` (`IWidgetNeuron`).", readmeContent);

            // Validate Module class content
            var moduleContent = File.ReadAllText(moduleClass);
            Assert.Contains("[DigitalBrain.Modules.Module(ManifestName)]", moduleContent);
            Assert.Contains("ManifestName = \"sample.widget\"", moduleContent);
            Assert.Contains("DigitalBrain.Modules.Secret.Named(\"ApiKey\")", moduleContent);
            Assert.Contains("DigitalBrain.Modules.Secret.Named(\"SigningToken\")", moduleContent);
            Assert.Contains("ActionDescriptor(\"sample.widget.execute\"", moduleContent);
            Assert.Contains("ActionDescriptor(\"sample.widget.status\"", moduleContent);

            // Validate Contracts interface content
            var interfaceContent = File.ReadAllText(contractsNeuron);
            Assert.Contains("[NeuronAlias(\"sample.widget.main\"), DefaultNeuronType(\"sample.widget.main\")]", interfaceContent);
            Assert.Contains("public interface IWidgetNeuron", interfaceContent);
            Assert.Contains("Execute(ExecuteRequest request", interfaceContent);
            Assert.Contains("Status(StatusRequest request", interfaceContent);
            Assert.Contains("public sealed record ExecuteRequest(string Payload);", interfaceContent);
            Assert.Contains("public sealed record ExecuteResult(string Detail, bool Success);", interfaceContent);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task ScaffoldsOnTestOSUsingNeuronProxy()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(RoslynModule).Assembly);
        var id = new DigitalBrain.NeuronId(new DigitalBrain.BrainId("test"), "roslyn.module-scaffolder", "probe");
        var scaffolder = os.Get<IModuleScaffolder>(id);
        Assert.NotNull(scaffolder);
    }
}
