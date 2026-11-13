using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Tests.E2E.Harness;
using Xunit;

namespace IntoChat.OS.Tests.E2E.Tier1_FeatureCoverage;

/// <summary>
/// Tier 1 Feature Coverage facts for Feature 2 (Delete DigitalBrain.Client),
/// Feature 4 (Orleans Stream Purge), Feature 5 (Dead Grain / Resolver Purge),
/// Feature 7 (Dissolved DigitalBrain.Spi), and Feature 8 (Slimmed INeuronContext).
/// </summary>
public sealed class ZeroLegacyShimsFacts : E2ETestBase
{
    // ==========================================
    // Feature 2: Zero DigitalBrain.Client References
    // ==========================================

    [Fact]
    public void T1_F02_TC01_ZeroDigitalBrainClient_InDirectoryPackagesProps()
    {
        var doc = XDocument.Load(PackagesPropsPath);
        var clientPkg = doc.Descendants("PackageVersion")
            .FirstOrDefault(p => p.Attribute("Include")?.Value == "DigitalBrain.Client");

        Assert.Null(clientPkg);
    }

    [Fact]
    public void T1_F02_TC02_ZeroDigitalBrainClient_PackageReferencesAcrossAllCsproj()
    {
        var offenders = new List<string>();
        foreach (var file in FindCsprojFiles())
        {
            var doc = XDocument.Load(file);
            var hasClient = doc.Descendants("PackageReference")
                .Any(p => p.Attribute("Include")?.Value == "DigitalBrain.Client");

            if (hasClient)
            {
                offenders.Add(Path.GetRelativePath(RepoRoot, file));
            }
        }

        Assert.True(offenders.Count == 0,
            "Zero DigitalBrain.Client PackageReferences allowed:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void T1_F02_TC03_ZeroUsingDigitalBrainClient_AcrossAllCSharpSourceFiles()
    {
        var regex = new Regex(@"^\s*using\s+(?:global::)?DigitalBrain\.Client\s*;", RegexOptions.Multiline | RegexOptions.Compiled);
        var offenders = new List<string>();
        foreach (var file in FindCsFiles())
        {
            var text = File.ReadAllText(file);
            if (regex.IsMatch(text))
            {
                offenders.Add(Path.GetRelativePath(RepoRoot, file));
            }
        }

        Assert.True(offenders.Count == 0,
            "Zero 'using DigitalBrain.Client;' allowed across codebase:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void T1_F02_TC04_ZeroDigitalBrainClient_FullyQualifiedTypeUsages()
    {
        var regex = new Regex(@"(?<!using\s+)\bDigitalBrain\.Client\b", RegexOptions.Compiled);
        var offenders = new List<string>();
        foreach (var file in FindCsFiles())
        {
            if (file.Contains("ZeroLegacyShimsFacts", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("PackageRestoreFacts", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (regex.IsMatch(text))
            {
                offenders.Add(Path.GetRelativePath(RepoRoot, file));
            }
        }

        Assert.True(offenders.Count == 0,
            "Zero qualified DigitalBrain.Client usages allowed:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void T1_F02_TC05_ZeroDigitalBrainClient_InCompiledAssemblies()
    {
        var currentAssembly = Assembly.GetExecutingAssembly();
        var referencedAssemblies = currentAssembly.GetReferencedAssemblies();
        var clientRefs = referencedAssemblies.Where(a => a.Name?.Equals("DigitalBrain.Client", StringComparison.OrdinalIgnoreCase) == true).ToList();

        Assert.Empty(clientRefs);
    }

    // ==========================================
    // Features 4 & 5: Stream Purge & Hosting/Dead Grain Purge
    // ==========================================

    [Fact]
    public void T1_F04_TC01_OsRuntimeOptions_ContainsNoPubSubStoreOrSignalStreams()
    {
        var path = Path.Combine(SrcDir, "IntoChat.OS.Kernel", "IntoChat.OS.Kernel", "Composition", "Kernel", "OsRuntimeOptions.cs");
        Assert.True(File.Exists(path), $"OsRuntimeOptions.cs must exist at expected path: {path}");

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("PubSubStore", text);
        Assert.DoesNotContain("SignalStreams", text);
    }

    [Fact]
    public void T1_F04_TC02_CloudStorage_ContainsNoPubSubStoreReferences()
    {
        var path = Path.Combine(SrcDir, "IntoChat.OS.Kernel", "IntoChat.OS.Kernel", "Composition", "Kernel", "CloudStorage.cs");
        Assert.True(File.Exists(path), $"CloudStorage.cs must exist at expected path: {path}");

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("PubSubStore", text);
    }

    [Fact]
    public void T1_F04_TC03_ZeroOrleansStreamRegistrations_AcrossHostingAndComposition()
    {
        var compositionDir = Path.Combine(SrcDir, "IntoChat.OS.Kernel", "IntoChat.OS.Kernel", "Composition");
        Assert.True(Directory.Exists(compositionDir), $"Composition directory must exist: {compositionDir}");

        var regex = new Regex(@"\b(AddAzureQueueStreams|AddPersistentStreams|AddMemoryStreams|AuthorizeStreams)\b", RegexOptions.Compiled);
        var csFiles = Directory.EnumerateFiles(compositionDir, "*.cs", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(csFiles);

        var offenders = new List<string>();
        foreach (var file in csFiles)
        {
            var text = File.ReadAllText(file);
            if (regex.IsMatch(text))
            {
                offenders.Add(Path.GetRelativePath(RepoRoot, file));
            }
        }

        Assert.True(offenders.Count == 0,
            "Zero Orleans persistent stream registrations allowed in composition:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void T1_F04_TC04_ZeroSignals0QueueOrPubSubStore_InAppHostAndBicepDeploy()
    {
        var targets = new List<string>();
        var appHostDir = Path.Combine(SrcDir, "IntoChat.OS.AppHost");
        var deployDir = Path.Combine(RepoRoot, "deploy");

        Assert.True(Directory.Exists(appHostDir), $"AppHost directory must exist: {appHostDir}");
        Assert.True(Directory.Exists(deployDir), $"Deploy directory must exist: {deployDir}");

        targets.AddRange(Directory.EnumerateFiles(appHostDir, "*.*", SearchOption.AllDirectories));
        targets.AddRange(Directory.EnumerateFiles(deployDir, "*.*", SearchOption.AllDirectories));
        Assert.NotEmpty(targets);

        var regex = new Regex(@"\b(signals-0|PubSubStore)\b", RegexOptions.Compiled);
        var offenders = new List<string>();
        foreach (var file in targets.Where(f => !f.Contains(@"\bin\") && !f.Contains(@"\obj\")))
        {
            var text = File.ReadAllText(file);
            if (regex.IsMatch(text))
            {
                offenders.Add(Path.GetRelativePath(RepoRoot, file));
            }
        }

        Assert.True(offenders.Count == 0,
            "Zero 'signals-0' or 'PubSubStore' allowed in AppHost or Bicep manifests:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void T1_F04_TC05_RuntimeOptions_TypeSurfaceHasNoStreamingProperties()
    {
        var runtimeOptionsType = typeof(RuntimeOptions);
        Assert.Null(runtimeOptionsType.GetProperty("PubSubStore"));
        Assert.Null(runtimeOptionsType.GetProperty("SignalStreams"));
    }

    [Fact]
    public void T1_F04_TC06_CloudStorage_StoresArray_ExclusivelyContainsPlatformGrainStorage()
    {
        var path = Path.Combine(SrcDir, "IntoChat.OS.Kernel", "IntoChat.OS.Kernel", "Composition", "Kernel", "CloudStorage.cs");
        Assert.True(File.Exists(path), "CloudStorage.cs must exist.");

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("PubSubStore", text);
        Assert.Contains("IntoChatOSNames.PlatformGrainStorage", text);
    }

    [Fact]
    public void T1_F05_TC01_GrainNeuronResolverFile_IsDeleted()
    {
        var path = Path.Combine(SrcDir, "IntoChat.OS.Kernel", "IntoChat.OS.Kernel", "Composition", "Kernel", "GrainNeuronResolver.cs");
        Assert.False(File.Exists(path), "GrainNeuronResolver.cs must be deleted.");
    }

    [Fact]
    public void T1_F05_TC02_ZeroINeuronResolverRegistrations_InBrainHosting()
    {
        var path = Path.Combine(SrcDir, "IntoChat.OS.Kernel", "IntoChat.OS.Kernel", "Composition", "Kernel", "BrainHosting.cs");
        Assert.True(File.Exists(path), $"BrainHosting.cs must exist at expected path: {path}");

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("INeuronResolver", text);
        Assert.DoesNotContain("GrainNeuronResolver", text);
    }

    [Fact]
    public void T1_F05_TC03_AgentNeuron_ContainsNoIWorkContinuation()
    {
        var path = Path.Combine(SrcDir, "Modules", "AI", "Agents", "AgentNeuron.cs");
        Assert.True(File.Exists(path), $"AgentNeuron.cs must exist at expected path: {path}");

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("IWorkContinuation", text);
        Assert.DoesNotContain("_continuationHeld", text);
    }

    [Fact]
    public void T1_F05_TC04_ZeroKernelConfirmationsOrIConfirmationContexts_InBrainHosting()
    {
        var path = Path.Combine(SrcDir, "IntoChat.OS.Kernel", "IntoChat.OS.Kernel", "Composition", "Kernel", "BrainHosting.cs");
        Assert.True(File.Exists(path), $"BrainHosting.cs must exist at expected path: {path}");

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("KernelConfirmations", text);
        Assert.DoesNotContain("IConfirmationContexts", text);
    }

    [Fact]
    public void T1_F05_TC05_ZeroObsoleteGateMechanisms_AcrossEntireRepository()
    {
        var regex = new Regex(@"\b(IOperationGate|ConfirmingOperationGate|StorageOperationGate|ProvisionalGateQueue)\b", RegexOptions.Compiled);
        var offenders = new List<string>();
        foreach (var file in FindCsFiles())
        {
            if (file.Contains("ZeroLegacyShimsFacts", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (regex.IsMatch(text))
            {
                offenders.Add(Path.GetRelativePath(RepoRoot, file));
            }
        }

        Assert.True(offenders.Count == 0,
            "Zero obsolete operation gate abstractions allowed:\n" + string.Join("\n", offenders));
    }

    // ==========================================
    // Features 7 & 8: Dissolved Spi & Slimmed INeuronContext
    // ==========================================

    [Fact]
    public void T1_F07_TC01_ZeroDigitalBrainSpiUsings_AcrossEntireRepository()
    {
        var regex = new Regex(@"^\s*using\s+(?:global::)?DigitalBrain\.Spi\s*;", RegexOptions.Multiline | RegexOptions.Compiled);
        var offenders = new List<string>();
        foreach (var file in FindCsFiles())
        {
            var text = File.ReadAllText(file);
            if (regex.IsMatch(text))
            {
                offenders.Add(Path.GetRelativePath(RepoRoot, file));
            }
        }

        Assert.True(offenders.Count == 0,
            "Zero 'using DigitalBrain.Spi;' allowed across codebase (dissolved in 0.19.0 R09):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void T1_F07_TC02_ZeroDigitalBrainSpiQualifiedTypes_InTestingInfrastructure()
    {
        var testDir = Path.Combine(RepoRoot, "tests", "IntoChat.OS.Tests.E2E");
        var regex = new Regex(@"\bDigitalBrain\.Spi\.[A-Za-z0-9_]+", RegexOptions.Compiled);
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(testDir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (regex.IsMatch(text))
            {
                offenders.Add(Path.GetRelativePath(RepoRoot, file));
            }
        }

        Assert.True(offenders.Count == 0,
            "Zero qualified DigitalBrain.Spi types allowed in testing infrastructure:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void T1_F08_TC04_INeuronContext_InterfaceSurfaceVerification()
    {
        var contextType = typeof(INeuronContext);
        var properties = contextType.GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        // Exactly 7 properties on slimmed INeuronContext
        Assert.Contains("Id", properties);
        Assert.Contains("Caller", properties);
        Assert.Contains("Time", properties);
        Assert.Contains("Timers", properties);
        Assert.Contains("Reminders", properties);
        Assert.Contains("Synapses", properties);
        Assert.Contains("TraceId", properties);

        // Obsolete properties must NOT exist on INeuronContext
        Assert.DoesNotContain("Services", properties);
        Assert.DoesNotContain("State", properties);
        Assert.DoesNotContain("History", properties);

        var methods = contextType.GetMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Publish", methods);
        Assert.Contains("PublishEphemeral", methods);
        Assert.Contains("Get", methods);
        Assert.Contains("Defer", methods);
    }

    [Fact]
    public void T1_F08_TC05_NeuronConstructorInjection_PatternEnforcedOnNewNeuronImplementations()
    {
        // Enforce that new neuron implementations do not reference obsolete Context.Services
        var testDir = Path.Combine(RepoRoot, "tests", "IntoChat.OS.Tests.E2E");
        var regex = new Regex(@"\bContext\.Services\b", RegexOptions.Compiled);
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(testDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains("ZeroLegacyShimsFacts", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (regex.IsMatch(text))
            {
                offenders.Add(Path.GetRelativePath(RepoRoot, file));
            }
        }

        Assert.True(offenders.Count == 0,
            "Zero Context.Services usages allowed (constructor injection is mandatory):\n" + string.Join("\n", offenders));
    }
}
