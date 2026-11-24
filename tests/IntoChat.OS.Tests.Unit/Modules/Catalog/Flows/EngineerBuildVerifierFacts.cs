using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

public sealed class EngineerBuildVerifierFacts : IDisposable
{
    private readonly string _root;

    public EngineerBuildVerifierFacts()
    {
        _root = Path.Combine(Path.GetTempPath(), "build-verifier-facts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void DiscoverTestProjects_SelectsReferencingTestProjectsPlusArchitectureFacts()
    {
        var webProj = WriteFile(
            "lib/Modules/Web/Web.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        WriteFile("lib/Modules/Web/Foo.cs", "namespace Web; public sealed class Foo {}");
        WriteFile(
            "lib/Modules/Other/Other.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var webTests = WriteFile(
            "tests/Web.Tests.Unit/Web.Tests.Unit.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="../../lib/Modules/Web/Web.csproj" />
              </ItemGroup>
            </Project>
            """);
        WriteFile(
            "tests/Other.Tests.Unit/Other.Tests.Unit.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="../../lib/Modules/Other/Other.csproj" />
              </ItemGroup>
            </Project>
            """);
        var archTests = WriteFile(
            "tests/IntoChat.OS.Architecture.Tests.Unit/IntoChat.OS.Architecture.Tests.Unit.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        WriteFile(
            "tests/Web.Tests.Browser/Web.Tests.Browser.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="../../lib/Modules/Web/Web.csproj" />
              </ItemGroup>
            </Project>
            """);

        string[] changedFiles = ["lib/Modules/Web/Foo.cs"];
        var affectedProjects = EngineerBuildVerifier.DiscoverAffectedProjects(_root, changedFiles);
        Assert.Equal([webProj], affectedProjects);

        var selected = EngineerBuildVerifier.DiscoverTestProjects(_root, changedFiles, affectedProjects);

        Assert.Equal(
            new[] { archTests, webTests }.Order(StringComparer.Ordinal).ToArray(),
            selected);
    }

    [Fact]
    public void DiscoverAffectedProjects_WalksUpwardFromCsprojAndJsonFiles()
    {
        var webProj = WriteFile(
            "lib/Modules/Web/Web.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        WriteFile("lib/Modules/Web/Assets/schema.json", "{}");

        var affectedFromJson = EngineerBuildVerifier.DiscoverAffectedProjects(_root, ["lib/Modules/Web/Assets/schema.json"]);
        Assert.Equal([webProj], affectedFromJson);

        var affectedFromCsproj = EngineerBuildVerifier.DiscoverAffectedProjects(_root, ["lib/Modules/Web/Web.csproj"]);
        Assert.Equal([webProj], affectedFromCsproj);
    }

    [Fact]
    public void DiscoverAffectedProjects_MapsComponentHostingToTheAppHost()
    {
        var appHostDir = SolutionPaths.Directory("IntoChat.OS.AppHost");
        var aspireDir = SolutionPaths.Directory("IntoChat.Aspire");
        var appHost = WriteFile(
            $"{appHostDir}/IntoChat.OS.AppHost.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var component = WriteFile(
            $"{aspireDir}/IntoChat.Aspire.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        WriteFile($"{aspireDir}/Hosting/AspireModuleHosting.cs", "class H {}");
        WriteFile($"{aspireDir}/Neurons/Telemetry/AspireNeuron.cs", "class N {}");
        var appHostTests = WriteFile(
            "tests/AppHost.Tests.Unit/AppHost.Tests.Unit.csproj",
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="../../{appHostDir}/IntoChat.OS.AppHost.csproj" />
              </ItemGroup>
            </Project>
            """);

        string[] hostingChange = [$"{aspireDir}/Hosting/AspireModuleHosting.cs"];
        var fromHosting = EngineerBuildVerifier.DiscoverAffectedProjects(_root, hostingChange);
        Assert.Equal([appHost], fromHosting);
        Assert.DoesNotContain(component, fromHosting);
        Assert.Contains(appHostTests, EngineerBuildVerifier.DiscoverTestProjects(_root, hostingChange, fromHosting));

        var fromNeuron = EngineerBuildVerifier.DiscoverAffectedProjects(_root, [$"{aspireDir}/Neurons/Telemetry/AspireNeuron.cs"]);
        Assert.Equal([component], fromNeuron);
    }

    private string WriteFile(string relativePath, string content)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }
}
