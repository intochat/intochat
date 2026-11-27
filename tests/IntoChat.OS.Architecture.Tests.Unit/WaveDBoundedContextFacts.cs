using System.Reflection;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using IntoChat.OS.Kernel;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Authoring;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Diagnostics;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Identity;
using IntoChat.OS.Modules.Runs;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Modules.UI.Controls;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Wave D — Domain Aggregate Re-Centering & Bounded Context Consolidation:
// Verifies the 7 cohesive DDD Bounded Contexts under src/Modules/, disjoint multi-module namespace ownership
// with zero duplicate Neuron or Connection claims, preserved Aggregate Root wire IDs, assembly replacement
// shims, sandbox custody isolation, and 100% preservation of all 14 server-side UI widget Orleans grains.
public sealed class WaveDBoundedContextFacts
{
    private static readonly string[] ExpectedBoundedContextModules =
    [
        "IntoChat.OS.Modules.AI",
        "IntoChat.OS.Modules.Identity",
        "IntoChat.OS.Modules.Infrastructure",
        "IntoChat.OS.Modules.Plugins",
        "IntoChat.OS.Modules.Roslyn",
        "IntoChat.OS.Modules.UI",
        "IntoChat.OS.Modules.Workspace",
    ];

    private static readonly string[] RetiredModuleProjects =
    [
        "IntoChat.OS.Modules.Catalog",
        "IntoChat.OS.Modules.Shell",
        "IntoChat.OS.Modules.Devices",
    ];

    [Fact]
    public void ModulesDirectoryContainsExactlySevenBoundedContexts()
    {
        var moduleProjects = Repository.Projects.Values
            .Where(project => Repository.IsUnder(project.Directory, Repository.ModulesRoot)
                && !project.Name.Contains(".Tests", StringComparison.Ordinal)
                && !project.Name.EndsWith(".Contracts", StringComparison.Ordinal)
                && !project.Name.EndsWith(".Generators", StringComparison.Ordinal))
            .Select(project => project.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedBoundedContextModules, moduleProjects);

        foreach (var retired in RetiredModuleProjects)
        {
            Assert.False(Repository.Projects.ContainsKey(retired), $"Retired scattered project '{retired}' must not remain in the solution.");
        }

        Assert.True(Repository.Projects.ContainsKey("IntoChat.OS.Modules.Identity.Contracts"));
        Assert.True(Repository.Projects.ContainsKey("IntoChat.OS.Modules.AI.Generators"));
    }

    [Fact]
    public void MultiModuleBoundedContextsMaintainDisjointNamespacesAndZeroDuplicateNeuronClaims()
    {
        var modules = OperatingSystemModules.Modules;
        var moduleTypes = modules.Select(m => m.Type).ToHashSet();

        Assert.Contains(typeof(IdentityModule), moduleTypes);
        Assert.Contains(typeof(AIModule), moduleTypes);
        Assert.Contains(typeof(DevicesModule), moduleTypes);
        Assert.Contains(typeof(AppsModule), moduleTypes);
        Assert.Contains(typeof(AuthoringModule), moduleTypes);
        Assert.Contains(typeof(DiagnosticsModule), moduleTypes);
        Assert.Contains(typeof(RunsModule), moduleTypes);
        Assert.Contains(typeof(FlutterModule), moduleTypes);
        Assert.Contains(typeof(UiModule), moduleTypes);

        // AI and Workspace are multi-module bounded contexts with disjoint module namespace prefixes.
        Assert.Equal("IntoChat.OS.Modules.AI", typeof(AIModule).Assembly.GetName().Name);
        Assert.Equal("IntoChat.OS.Modules.AI", typeof(DevicesModule).Assembly.GetName().Name);
        Assert.Equal("IntoChat.OS.Modules.Workspace", typeof(AppsModule).Assembly.GetName().Name);
        Assert.Equal("IntoChat.OS.Modules.Workspace", typeof(AuthoringModule).Assembly.GetName().Name);
        Assert.Equal("IntoChat.OS.Modules.Workspace", typeof(DiagnosticsModule).Assembly.GetName().Name);
        Assert.Equal("IntoChat.OS.Modules.Workspace", typeof(RunsModule).Assembly.GetName().Name);
        Assert.Equal("IntoChat.OS.Modules.Workspace", typeof(FlutterModule).Assembly.GetName().Name);

        // Every multi-namespace bounded context assembly under src/Modules/ must have strictly disjoint [Module] namespace prefixes
        // (matching NeuronTypeRegistry.HasDisjointNamespaces).
        var declaredModuleEntries = modules
            .Where(m => m.Type.Assembly.GetName().Name?.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal) == true
                && typeof(DigitalBrain.Modules.Module).IsAssignableFrom(m.Type)
                && m.Type.IsDefined(typeof(ModuleAttribute), inherit: false))
            .ToArray();

        string[] expectedMultiContextAssemblies =
        [
            "IntoChat.OS.Modules.AI",
            "IntoChat.OS.Modules.Infrastructure",
            "IntoChat.OS.Modules.Plugins",
            "IntoChat.OS.Modules.Workspace",
        ];

        var multiNamespaceGroups = declaredModuleEntries
            .GroupBy(m => m.Type.Assembly)
            .Where(g => g.Select(m => m.Type.Namespace).Distinct(StringComparer.Ordinal).Count() > 1)
            .ToArray();

        Assert.Equal(
            expectedMultiContextAssemblies,
            multiNamespaceGroups.Select(g => g.Key.GetName().Name!).Order(StringComparer.Ordinal).ToArray());

        foreach (var group in multiNamespaceGroups)
        {
            var descriptors = group.ToArray();
            var distinctNamespaces = descriptors
                .Select(d => d.Type.Namespace)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            foreach (var ns in distinctNamespaces)
            {
                Assert.False(string.IsNullOrWhiteSpace(ns), $"Module in {group.Key.GetName().Name} must declare a namespace.");
            }

            // In AI and Workspace bounded contexts, every [Module] has its own distinct namespace.
            if (group.Key.GetName().Name is "IntoChat.OS.Modules.AI" or "IntoChat.OS.Modules.Workspace")
            {
                Assert.Equal(descriptors.Length, distinctNamespaces.Length);
            }

            for (var i = 0; i < distinctNamespaces.Length; i++)
            {
                var left = distinctNamespaces[i]!;
                for (var j = i + 1; j < distinctNamespaces.Length; j++)
                {
                    var right = distinctNamespaces[j]!;
                    Assert.False(
                        left.StartsWith(right + ".", StringComparison.Ordinal)
                        || right.StartsWith(left + ".", StringComparison.Ordinal),
                        $"Namespaces '{left}' and '{right}' in {group.Key.GetName().Name} overlap (nested).");
                }
            }
        }

        // Across all composed [Module] assemblies under src/Modules/, every [Neuron] type identifier must be globally unique
        // and, in multi-namespace bounded contexts, claimed by exactly one declared [Module] namespace (matching NeuronTypeRegistry).
        var claimedNeuronIds = new Dictionary<string, (string Owner, Type NeuronType)>(StringComparer.Ordinal);
        foreach (var group in declaredModuleEntries.GroupBy(m => m.Type.Assembly))
        {
            var declaredInAssembly = group.Key.GetTypes()
                .Where(static t => t is { IsClass: true, IsAbstract: false }
                    && typeof(DigitalBrain.Modules.Module).IsAssignableFrom(t)
                    && t.IsDefined(typeof(ModuleAttribute), inherit: false))
                .ToArray();
            var distinctNamespaces = declaredInAssembly.Select(m => m.Namespace!).Distinct(StringComparer.Ordinal).ToArray();
            var isMultiNamespace = distinctNamespaces.Length > 1;
            var neuronTypes = group.Key.GetTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetCustomAttribute<NeuronAttribute>(inherit: false) is not null)
                .ToArray();

            foreach (var neuronType in neuronTypes)
            {
                var neuronId = neuronType.GetCustomAttribute<NeuronAttribute>(inherit: false)!.Type;
                string owner;
                if (isMultiNamespace)
                {
                    var owningNamespaces = distinctNamespaces.Where(ns => OwnsNamespace(ns, neuronType.Namespace)).ToArray();
                    Assert.True(
                        owningNamespaces.Length == 1,
                        $"Neuron '{neuronId}' ({neuronType.FullName}) in {group.Key.GetName().Name} matched {owningNamespaces.Length} owning module namespace(s): {string.Join(", ", owningNamespaces)}.");
                    owner = owningNamespaces[0];
                }
                else
                {
                    owner = group.Key.GetName().Name!;
                }

                if (claimedNeuronIds.TryGetValue(neuronId, out var existing))
                {
                    Assert.Fail($"Duplicate Neuron type '{neuronId}' claimed by {existing.Owner} ({existing.NeuronType.FullName}) and {owner} ({neuronType.FullName}).");
                }

                claimedNeuronIds[neuronId] = (owner, neuronType);
            }

            // Every IConnection in a multi-namespace bounded context assembly must also map to a single owning module namespace.
            if (isMultiNamespace)
            {
                var connectionTypes = group.Key.GetTypes()
                    .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IConnection).IsAssignableFrom(t))
                    .ToArray();
                foreach (var connectionType in connectionTypes)
                {
                    var owningNamespaces = distinctNamespaces.Where(ns => OwnsNamespace(ns, connectionType.Namespace)).ToArray();
                    Assert.True(
                        owningNamespaces.Length == 1,
                        $"Connection '{connectionType.FullName}' in {group.Key.GetName().Name} matched {owningNamespaces.Length} owning module namespace(s).");
                }
            }
        }
    }

    [Fact]
    public void AggregateRootBoundariesAndWireIdsArePreservedAcrossBoundedContexts()
    {
        (string AssemblyName, string TypeName, string? ExpectedNeuronId)[] expectedAggregateRoots =
        [
            // Identity Bounded Context (strictly security-fenced; identity.vault unified on DigitalBrain.Identity.Local.Vault.VaultNeuron)
            ("IntoChat.OS.Modules.Identity", "BrainKeysNeuron", "identity.brain-keys"),
            ("IntoChat.OS.Modules.Identity", "IdentityDirectoryGrain", "identity.directory"),
            ("IntoChat.OS.Modules.Identity", "GrantsNeuron", "grants.default"),
            ("IntoChat.OS.Modules.Identity", "HandleDirectoryNeuron", "identity.handles"),

            // AI & Compute Bounded Context (AI + Devices)
            ("IntoChat.OS.Modules.AI", "ConversationNeuron", "conversation"),
            ("IntoChat.OS.Modules.AI", "AgentContextNeuron", "agent-context"),
            ("IntoChat.OS.Modules.AI", "MemoryNeuron", "ai.memory"),
            ("IntoChat.OS.Modules.AI", "KnowledgeStoreNeuron", "ai.knowledge-store"),
            ("IntoChat.OS.Modules.AI", "LlmNeuron", "ai.llm"),
            ("IntoChat.OS.Modules.AI", "ModelCatalogNeuron", "ai.models"),
            ("IntoChat.OS.Modules.AI", "DeviceNeuron", "devices.device"),
            ("IntoChat.OS.Modules.AI", "DeviceEnrollmentNeuron", "devices.enrollment"),
            ("IntoChat.OS.Modules.AI", "DeviceLinkNeuron", "devices.link"),

            // Workspace Bounded Context (Apps + Authoring + Diagnostics + Runs + Shell/Flutter)
            ("IntoChat.OS.Modules.Workspace", "WorkspaceNeuron", "ui.workspace"),
            ("IntoChat.OS.Modules.Workspace", "ScreenDirectoryNeuron", "ui.screens"),
            ("IntoChat.OS.Modules.Workspace", "ViewNeuron", "view"),
            ("IntoChat.OS.Modules.Workspace", "ViewportNeuron", "viewport"),
            ("IntoChat.OS.Modules.Workspace", "WebBrowserNeuron", "webbrowser"),
            ("IntoChat.OS.Modules.Workspace", "InstalledAppsNeuron", "apps.installed"),
            ("IntoChat.OS.Modules.Workspace", "ApprovalsNeuron", "approvals"),
            ("IntoChat.OS.Modules.Workspace", "RouterNeuron", "ai.router"),
            ("IntoChat.OS.Modules.Workspace", "FilesWorkspaceScreen", "app.files"),
            ("IntoChat.OS.Modules.Workspace", "FriendsNeuron", "friends.links"),
            ("IntoChat.OS.Modules.Workspace", "PostsNeuron", "social.posts"),
            ("IntoChat.OS.Modules.Workspace", "RoomNeuron", "social.room"),
            ("IntoChat.OS.Modules.Workspace", "RoomsNeuron", "social.rooms"),
            ("IntoChat.OS.Modules.Workspace", "DebuggerReadsNeuron", "creator.reads"),
            ("IntoChat.OS.Modules.Workspace", "TraceReaderNeuron", "trace.reader"),
            ("IntoChat.OS.Modules.Workspace", "EngineerRunsNeuron", "engineer.runs"),
            ("IntoChat.OS.Modules.Workspace", "SelfImprovementLoop", "loop.self-improvement"),

            // UI Bounded Context (all 14 server-side UI widget Orleans grains preserved intact)
            ("IntoChat.OS.Modules.UI", "ChatThreadNeuron", "control.chatThread"),
            ("IntoChat.OS.Modules.UI", "CheckBoxNeuron", "control.checkBox"),
            ("IntoChat.OS.Modules.UI", "ComboBoxNeuron", "control.comboBox"),
            ("IntoChat.OS.Modules.UI", "ExpanderNeuron", "control.expander"),
            ("IntoChat.OS.Modules.UI", "FormNeuron", "control.form"),
            ("IntoChat.OS.Modules.UI", "KanbanNeuron", "control.kanban"),
            ("IntoChat.OS.Modules.UI", "ListBoxNeuron", "control.listBox"),
            ("IntoChat.OS.Modules.UI", "LiveChartNeuron", "control.liveChart"),
            ("IntoChat.OS.Modules.UI", "QueryTableNeuron", "control.queryTable"),
            ("IntoChat.OS.Modules.UI", "SliderNeuron", "control.slider"),
            ("IntoChat.OS.Modules.UI", "StepperNeuron", "control.stepper"),
            ("IntoChat.OS.Modules.UI", "TabStripNeuron", "control.tabStrip"),
            ("IntoChat.OS.Modules.UI", "ToggleSwitchNeuron", "control.toggleSwitch"),
            ("IntoChat.OS.Modules.UI", "TreeNeuron", "control.tree"),
        ];

        var assembliesByName = new[]
        {
            typeof(IdentityModule).Assembly,
            typeof(AIModule).Assembly,
            typeof(AppsModule).Assembly,
            typeof(UiModule).Assembly,
        }.ToDictionary(a => a.GetName().Name!, StringComparer.Ordinal);

        foreach (var (assemblyName, typeName, expectedNeuronId) in expectedAggregateRoots)
        {
            var assembly = assembliesByName[assemblyName];
            var match = assembly.GetTypes().SingleOrDefault(t => !t.IsNested && t.Name == typeName);
            Assert.True(match is not null, $"Aggregate root '{typeName}' was not found in bounded context assembly '{assemblyName}'.");

            if (expectedNeuronId is not null)
            {
                var neuronAttr = match!.GetCustomAttribute<NeuronAttribute>(inherit: false);
                Assert.NotNull(neuronAttr);
                Assert.Equal(expectedNeuronId, neuronAttr.Type);
            }
        }

        // Verify all 14 server-side UI widget Orleans grains remain in IntoChat.OS.Modules.UI.
        var uiControls = typeof(UiModule).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                && t.Namespace == typeof(ICheckBoxControl).Namespace
                && t.GetCustomAttribute<NeuronAttribute>(inherit: false)?.Type.StartsWith("control.", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(14, uiControls.Length);
    }

    [Fact]
    public void ConsolidatedBoundedContextsDeclareAssemblyReplacementMetadata()
    {
        AssertReplaces(typeof(AIModule).Assembly, "IntoChat.OS.Modules.Devices");
        AssertReplaces(
            typeof(AppsModule).Assembly,
            "IntoChat.OS.Modules.Catalog",
            "IntoChat.OS.Modules.Shell",
            "IntoChat.OS.Modules.Authoring",
            "IntoChat.OS.Modules.Runs",
            "IntoChat.OS.Modules.Diagnostics");
    }

    [Fact]
    public void SecuritySandboxPreservesIdentityAndHostCustodyIsolation()
    {
        var sandboxProject = Repository.ProjectNamed("IntoChat.OS.Sandbox");
        var appLoadContextSource = File.ReadAllText(Path.Combine(sandboxProject.Directory, "AppLoadContext.cs"));

        string[] requiredForbiddenPrefixes =
        [
            "\"IntoChat.OS.Kernel\"",
            "\"IntoChat.OS.Host\"",
            "\"IntoChat.OS.Modules.Identity\"",
            "\"IntoChat.OS.Sandbox\"",
            "\"DigitalBrain.Runtime.Orleans\"",
            "\"DigitalBrain.Identity\"",
        ];

        foreach (var requiredPrefix in requiredForbiddenPrefixes)
        {
            Assert.Contains(requiredPrefix, appLoadContextSource, StringComparison.Ordinal);
        }

        // Identity.Contracts stays a dedicated security-fenced assembly and is never replaced by IntoChat.OS.Contracts.
        var contractsAssembly = Assembly.Load("IntoChat.OS.Contracts");
        var replacedByContracts = contractsAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attr => attr.Key == "DigitalBrain.ReplacesAssembly" && !string.IsNullOrWhiteSpace(attr.Value))
            .Select(attr => attr.Value!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("IntoChat.OS.Modules.Identity.Contracts", replacedByContracts);
        Assert.Equal("IntoChat.OS.Modules.Identity.Contracts", typeof(IntoChat.OS.Modules.Identity.Contracts.AccountId).Assembly.GetName().Name);
    }

    private static bool OwnsNamespace(string moduleNamespace, string? typeNamespace)
        => typeNamespace is not null
            && (string.Equals(typeNamespace, moduleNamespace, StringComparison.Ordinal)
                || typeNamespace.StartsWith(moduleNamespace + ".", StringComparison.Ordinal));

    private static void AssertReplaces(Assembly assembly, params string[] expectedReplacedAssemblies)
    {
        var replaced = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attr => attr.Key == "DigitalBrain.ReplacesAssembly" && !string.IsNullOrWhiteSpace(attr.Value))
            .Select(attr => attr.Value!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var expected in expectedReplacedAssemblies)
        {
            Assert.True(
                replaced.Contains(expected),
                $"Assembly '{assembly.GetName().Name}' must declare DigitalBrain.ReplacesAssembly='{expected}'.");
        }
    }
}
