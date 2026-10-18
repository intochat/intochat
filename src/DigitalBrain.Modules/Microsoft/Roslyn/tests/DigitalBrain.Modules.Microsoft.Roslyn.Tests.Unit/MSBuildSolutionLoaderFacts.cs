using System.Diagnostics;
using DigitalBrain.Microsoft.Roslyn;
using Microsoft.CodeAnalysis;

namespace DigitalBrain.Modules.Microsoft.Roslyn.Tests.Unit;

public sealed class MSBuildSolutionLoaderFacts
{
    [Fact]
    public async Task SdkSolutionLoadsSourceAndFrameworkReferences()
    {
        var directory = Directory.CreateTempSubdirectory("roslyn-loader-");
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "Sample.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup>
                </Project>
                """, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "Sample.slnx"), """
                <Solution><Project Path="Sample.csproj" /></Solution>
                """, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "Sample.cs"),
                "public sealed class Sample { public string Name => System.String.Empty; }", cancellationToken);
            // The SDK reference pack is local; this fixture must not depend on a remote feed.
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "NuGet.Config"),
                "<configuration><packageSources><clear /></packageSources></configuration>", cancellationToken);
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("restore");
            start.ArgumentList.Add("Sample.csproj");
            start.ArgumentList.Add("--disable-build-servers");
            DotNetTestProcess.DisableBuildServerReuse(start);
            using (var restore = Process.Start(start)!)
            {
                var output = restore.StandardOutput.ReadToEndAsync(cancellationToken);
                var error = restore.StandardError.ReadToEndAsync(cancellationToken);
                await restore.WaitForExitAsync(cancellationToken);
                Assert.True(restore.ExitCode == 0, await output + await error);
            }

            var loaded = await new MSBuildSolutionLoader().OpenAsync(
                Path.Combine(directory.FullName, "Sample.slnx"), new Progress<string>(), cancellationToken);
            using var workspace = loaded.Workspace;
            Assert.Empty(loaded.Failures);
            var project = Assert.Single(workspace.CurrentSolution.Projects);
            Assert.Contains(project.Documents, document => document.Name == "Sample.cs");
            var compilation = await project.GetCompilationAsync(cancellationToken);
            Assert.NotNull(compilation);
            Assert.NotNull(compilation.GetTypeByMetadataName("Sample"));
            Assert.Empty(compilation.GetDiagnostics(cancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
