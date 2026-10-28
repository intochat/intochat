using DigitalBrain.Apps.Packaging;

namespace IntoChat.OS.Cli.Tests.Unit;

public sealed class AppBuildFacts
{
    [Fact]
    public async Task RealBuildUsesEvaluatedOutputAndSurvivesSourceRemoval()
    {
        var root = NewRoot();
        try
        {
            var project = await Project(root);
            var build = new AppBuild(Path.Combine(root, "cache"));
            var package = await build.Build(project, "Release", TestContext.Current.CancellationToken);
            Assert.Equal("fixture.app", package.Descriptor.Id);
            Assert.Equal("[\"open\"]", package.Descriptor.Presentation["operations"]);
            Assert.Equal("App.With.Space.dll", package.Descriptor.EntryAssembly);
            Assert.Equal("Fixture.AppJsonContext", package.Descriptor.HostMetadata["jsonContext"]);
            Assert.Contains(package.Descriptor.SharedAssemblies, p => p.Key == "DigitalBrain");
            Assert.Contains(package.Descriptor.Payload, p => p.Path == "PrivateHelper.dll");
            Assert.Contains(package.Descriptor.Payload, p => p.Path == "assets/welcome.txt");
            Assert.DoesNotContain(package.Descriptor.Payload, p => p.Path.EndsWith(".cs", StringComparison.Ordinal) || p.Path.EndsWith(".pdb", StringComparison.Ordinal) || p.Path == "DigitalBrain.dll" || p.Path.Contains("Analyzers", StringComparison.Ordinal));
            foreach (var payload in package.Descriptor.Payload)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(package.DirectoryPath, payload.Path), TestContext.Current.CancellationToken);
                Assert.DoesNotContain(root, System.Text.Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(root, System.Text.Encoding.Unicode.GetString(bytes), StringComparison.OrdinalIgnoreCase);
            }
            var output = Path.Combine(root, "out", "app.intochat-app");
            await build.Pack(project, output, TestContext.Current.CancellationToken);
            Directory.Delete(Path.GetDirectoryName(project)!, true);
            await using var archive = File.OpenRead(output);
            var reread = await new AppPackageReader(sharedAssemblies: package.Descriptor.SharedAssemblies).Read(archive, Path.Combine(root, "received"), TestContext.Current.CancellationToken);
            Assert.Equal(package.Descriptor.Id, reread.Descriptor.Id);
        }
        finally { Clean(root); }
    }

    [Theory]
    [InlineData("missing-context", "JsonSerializerContext")]
    [InlineData("identity-mismatch", "does not match")]
    [InlineData("missing-input", "Fixture.Input")]
    public async Task InvalidMetadataFailsActionably(string mode, string diagnostic)
    {
        var root = NewRoot();
        try
        {
            var project = await Project(root, mode);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new AppBuild(Path.Combine(root, "cache")).Build(project, "Release", TestContext.Current.CancellationToken));
            Assert.Contains(diagnostic, error.Message, StringComparison.Ordinal);
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task FailedBuildPreservesAnExistingArtifact()
    {
        var root = NewRoot();
        try
        {
            var project = await Project(root);
            var output = Path.Combine(root, "app.intochat-app");
            await File.WriteAllTextAsync(output, "previous artifact", TestContext.Current.CancellationToken);
            await File.AppendAllTextAsync(Path.Combine(Path.GetDirectoryName(project)!, "App.cs"), "\nthis is not C#;", TestContext.Current.CancellationToken);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new AppBuild(Path.Combine(root, "cache")).Pack(project, output, TestContext.Current.CancellationToken));
            Assert.Contains("exited", error.Message, StringComparison.Ordinal);
            Assert.Equal("previous artifact", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        }
        finally { Clean(root); }
    }

    private static async Task<string> Project(string root, string mode = "valid")
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Intochat.OS.slnx"))) { repo = repo.Parent; }
        if (repo is null) { throw new InvalidOperationException("Repository not found."); }
        var directory = Path.Combine(root, "source with spaces");
        Directory.CreateDirectory(directory);
        var core = Path.Combine(repo.FullName, "framework", "src", "DigitalBrain", "DigitalBrain.csproj");
        var analyzer = Path.Combine(repo.FullName, "framework", "src", "DigitalBrain.Analyzers", "DigitalBrain.Analyzers.csproj");
        var helper = Path.Combine(root, "private dependency");
        Directory.CreateDirectory(helper);
        await File.WriteAllTextAsync(Path.Combine(helper, "PrivateHelper.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(helper, "Helper.cs"), "public static class Helper { public static string Message => \"hello\"; }");
        Directory.CreateDirectory(Path.Combine(directory, "assets"));
        await File.WriteAllTextAsync(Path.Combine(directory, "assets", "welcome.txt"), "Hello from app asset");
        var project = Path.Combine(directory, "App.csproj");
        await File.WriteAllTextAsync(project, $"""
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>App.With.Space</AssemblyName><DigitalBrainCompiledApp>true</DigitalBrainCompiledApp><OutputPath>custom evaluated output/</OutputPath></PropertyGroup>
            <ItemGroup><AdditionalFiles Include="FixtureApp.spec.md"/><Content Include="assets/welcome.txt" CopyToOutputDirectory="PreserveNewest"/><ProjectReference Include="{Path.Combine(helper, "PrivateHelper.csproj")}"/><CompilerVisibleProperty Include="DigitalBrainCompiledApp"/><ProjectReference Include="{core}"/><ProjectReference Include="{analyzer}" ReferenceOutputAssembly="false" OutputItemType="Analyzer"/></ItemGroup></Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(directory, "FixtureApp.spec.md"), "# FixtureApp\nRole: Optional\nShipped: false\nOperations: open\n\n## Scenario: Open\nOpens the app.\n");
        var context = mode == "missing-context" ? "" : $$"""
            [System.Text.Json.Serialization.JsonSerializable(typeof({{(mode == "missing-input" ? "string" : "Input")}}))]
            public partial class AppJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
            """;
        var manifest = mode == "identity-mismatch" ? "public static AppManifest Manifest => new(\"wrong.id\", \"App\", \"1.0.0\", [], new([]));" : "";
        await File.WriteAllTextAsync(Path.Combine(directory, "App.cs"), $$"""
            using DigitalBrain;
            namespace Fixture;
            [Signal("fixture.input")]
            public sealed record Input([property: DigitalBrain.Contracts.Field(0)] string Text) : Signal;
            [App("fixture.app")]
            public sealed class FixtureApp : App
            {
                {{manifest}}
                [On] public Task Open(Input input) => Task.CompletedTask;
            }
            {{context}}
            """);
        return project;
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "intochat cli " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    private static void Clean(string root)
    {
        if (!Directory.Exists(root)) { return; }
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) { File.SetAttributes(file, FileAttributes.Normal); }
        Directory.Delete(root, true);
    }
}
