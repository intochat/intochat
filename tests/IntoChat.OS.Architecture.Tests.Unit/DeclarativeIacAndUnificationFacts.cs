using System.Xml.Linq;
using IntoChat.OS.Kernel.Install;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Wave B guard rails:
// 1. Declarative Pulumi IaC templates live under deploy/ and POSIX az retry/handover logic lives in scripts/az-retry.sh
//    (LF endings, embedded into IntoChat.OS.Kernel with zero inline bash StringBuilder duplication).
// 2. Opt-in cross-repo ProjectReference unification (eng/IntoChat.OS.Projects.props + ProjectReferences.targets + Unified.proj)
//    maps every packable IntoChat.OS.* and DigitalBrain.* library/module/component project while preserving NuGet defaults.
public sealed class DeclarativeIacAndUnificationFacts
{
    [Fact]
    public void DeclarativePulumiTemplatesAndAzRetryScriptExistWithLfAndMatchEmbeddedAssets()
    {
        var files = new (string RelativePath, string EmbeddedContent)[]
        {
            ("deploy/Pulumi.yaml", PulumiAssets.PulumiYamlTemplate),
            ("deploy/Pulumi.stack.yaml", PulumiAssets.StackConfigYamlTemplate),
            ("deploy/command-stop.yaml", PulumiAssets.CommandStopYamlTemplate),
            ("deploy/command-start.yaml", PulumiAssets.CommandStartYamlTemplate),
            ("scripts/az-retry.sh", PulumiAssets.AzRetryScript),
        };

        foreach (var (relativePath, embeddedContent) in files)
        {
            var fullPath = Path.Combine(Repository.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fullPath), $"Declarative IaC asset '{relativePath}' must exist.");
            var rawBytes = File.ReadAllBytes(fullPath);
            Assert.False(rawBytes.Contains((byte)'\r'), $"Declarative IaC asset '{relativePath}' must use LF line endings.");
            var diskText = File.ReadAllText(fullPath).ReplaceLineEndings("\n");
            Assert.Equal(diskText, embeddedContent);
        }

        var script = PulumiAssets.AzRetryScript;
        Assert.StartsWith("#!/bin/sh\n", script, StringComparison.Ordinal);
        Assert.Contains("az_retry()", script, StringComparison.Ordinal);
        Assert.Contains("az_stop_revisions()", script, StringComparison.Ordinal);
        Assert.Contains("az_start_revision()", script, StringComparison.Ordinal);
        Assert.Contains("sleep $((_az_attempt * 2))", script, StringComparison.Ordinal);
        Assert.Contains("*RevisionAlreadyInRequestedState*", script, StringComparison.Ordinal);
        Assert.Contains("*ResourceNotFound*|*ResourceGroupNotFound*", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PulumiStackSynthesizerDoesNotHardcodeInlineAzRetryBash()
    {
        var kernelDir = Repository.ProjectNamed("IntoChat.OS.Kernel").Directory;
        var yamlSynthesizer = File.ReadAllText(Path.Combine(kernelDir, "Install", "PulumiStackSynthesizer.Yaml.cs"));
        var dockerfile = File.ReadAllText(Path.Combine(kernelDir, "Dockerfile"));

        Assert.DoesNotContain("az_retry() {", yamlSynthesizer, StringComparison.Ordinal);
        Assert.Contains("PulumiAssets.", yamlSynthesizer, StringComparison.Ordinal);
        Assert.Contains("COPY deploy/ ./deploy/", dockerfile, StringComparison.Ordinal);
        Assert.Contains("COPY scripts/ ./scripts/", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectReferenceUnificationCatalogCoversEveryPackableProjectAndExistsOnDisk()
    {
        var propsPath = Path.Combine(Repository.Root, "eng", "IntoChat.OS.Projects.props");
        var targetsPath = Path.Combine(Repository.Root, "eng", "IntoChat.OS.ProjectReferences.targets");
        var unifiedProjPath = Path.Combine(Repository.Root, "eng", "IntoChat.OS.Unified.proj");
        var rootBuildProps = File.ReadAllText(Path.Combine(Repository.Root, "Directory.Build.props"));

        Assert.True(File.Exists(propsPath), "eng/IntoChat.OS.Projects.props must exist.");
        Assert.True(File.Exists(targetsPath), "eng/IntoChat.OS.ProjectReferences.targets must exist.");
        Assert.True(File.Exists(unifiedProjPath), "eng/IntoChat.OS.Unified.proj must exist.");

        Assert.Contains("<IntoChatOSRepoRoot", rootBuildProps, StringComparison.Ordinal);
        Assert.Contains("<IntoChatOSUseProjectReferences Condition=\"'$(IntoChatOSUseProjectReferences)' == ''\">false</IntoChatOSUseProjectReferences>", rootBuildProps, StringComparison.Ordinal);

        var propsDoc = XDocument.Load(propsPath);
        _ = XDocument.Load(targetsPath);
        _ = XDocument.Load(unifiedProjPath);

        var mapped = propsDoc.Descendants("IntoChatOSProjectMap")
            .Select(e => (
                PackageId: e.Attribute("Include")?.Value ?? "",
                ProjectPath: e.Attribute("ProjectPath")?.Value ?? "",
                IsAnalyzer: e.Attribute("IsAnalyzer")?.Value ?? ""))
            .ToArray();

        Assert.NotEmpty(mapped);
        Assert.Equal(mapped.Length, mapped.Select(m => m.PackageId).Distinct(StringComparer.Ordinal).Count());

        const string repoRootToken = "$(IntoChatOSRepoRoot)";
        foreach (var entry in mapped)
        {
            Assert.StartsWith(repoRootToken, entry.ProjectPath, StringComparison.Ordinal);
            var relativeCsproj = entry.ProjectPath[repoRootToken.Length..];
            var fullCsproj = Path.Combine(Repository.Root, relativeCsproj.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fullCsproj), $"Mapped project for '{entry.PackageId}' does not exist on disk: {relativeCsproj}");
            var expectedAnalyzer = string.Equals(entry.PackageId, "DigitalBrain.Analyzers", StringComparison.Ordinal) ? "true" : "false";
            Assert.Equal(expectedAnalyzer, entry.IsAnalyzer);
        }

        var mappedIds = mapped.Select(m => m.PackageId).ToHashSet(StringComparer.Ordinal);

        // Every packable OS + module + component project must be mapped.
        var osPackable = PackageRootAllowlistFacts.PackableProjects().Select(p => p.PackageId).ToArray();
        var missingOs = osPackable.Where(id => !mappedIds.Contains(id)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(missingOs.Length == 0, "Packable OS projects missing from eng/IntoChat.OS.Projects.props:\n" + string.Join('\n', missingOs));

        // Every packable framework library project (excluding dotnet new templates) must be mapped.
        var frameworkEntries = Repository.Solution.Descendants("Project")
            .Where(Repository.IsFrameworkEntry)
            .Select(e => e.Attribute("Path")!.Value.Replace('\\', '/'))
            .Where(path => !path.StartsWith("framework/templates/", StringComparison.Ordinal))
            .ToArray();
        var missingFramework = new List<string>();
        foreach (var relPath in frameworkEntries)
        {
            var doc = XDocument.Load(Path.Combine(Repository.Root, relPath.Replace('/', Path.DirectorySeparatorChar)));
            var isPackable = doc.Descendants("IsPackable").LastOrDefault()?.Value.Trim();
            if (!string.Equals(isPackable, "true", StringComparison.Ordinal))
            {
                continue;
            }
            var packageId = doc.Descendants("PackageId").LastOrDefault()?.Value.Trim()
                ?? Path.GetFileNameWithoutExtension(relPath);
            if (!mappedIds.Contains(packageId))
            {
                missingFramework.Add(packageId);
            }
        }
        Assert.True(missingFramework.Count == 0, "Packable framework projects missing from eng/IntoChat.OS.Projects.props:\n" + string.Join('\n', missingFramework));
    }
}
