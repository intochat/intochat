using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using DigitalBrain.Analyzers.Components;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DigitalBrain.Analyzers.Tests.Unit.Components;

public sealed class SlotGeneratorFacts
{
    private const string Prelude = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;

        namespace DigitalBrain
        {
            [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, Inherited = false)]
            public sealed class SlotAttribute : Attribute
            {
                public string? Name { get; init; }
                public string? Key { get; set; }
                public bool Required { get; set; } = true;
            }
        }

        namespace DigitalBrain.Components
        {
            public readonly record struct ComponentId(string Value);

            public sealed record SlotNeed(string Name, Type Contract, bool Required = true, string? Key = null);

            public sealed record ComponentManifest(ComponentId Id, SlotNeed[] Slots);

            public static class ComponentRegistry
            {
                public static readonly Dictionary<Type, ComponentId> All = new();
                public static void Register(Type neuronClass, ComponentId component) => All[neuronClass] = component;
            }

            public static class ComponentManifests
            {
                public static readonly Dictionary<ComponentId, ComponentManifest> All = new();
                public static void Register(Type neuronClass, ComponentManifest manifest) => All[manifest.Id] = manifest;
            }

            public sealed class Slots
            {
                public string? LastSlot { get; private set; }
                public T Get<T>(string slot) where T : class
                {
                    LastSlot = slot;
                    return default!;
                }
            }
        }

        namespace DigitalBrain.Neurons
        {
            [AttributeUsage(AttributeTargets.Class, Inherited = false)]
            public sealed class NeuronAttribute(string type) : Attribute
            {
                public string Type { get; } = type;
            }

            public abstract class Neuron
            {
                public DigitalBrain.Components.Slots Slots { get; } = new();
            }
        }
        """;

    private static MetadataReference[] References =>
    [
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
        MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
        MetadataReference.CreateFromFile(Assembly.Load("System.Collections").Location),
    ];

    [Fact]
    public void ASlotPropertyGetsAGetterAndAManifestEntry()
    {
        const string source = """
            using DigitalBrain;
            using DigitalBrain.Neurons;

            namespace Sample;

            public interface ISearch { }
            public interface ITable { }
            public interface IOptionalCache { }

            [Neuron("app.crm")]
            public sealed partial class CrmNeuron : Neuron
            {
                [Slot]
                private partial ISearch Search { get; }

                [Slot(Key = "customer_profiles")]
                private partial ITable Customers { get; }

                [Slot(Required = false)]
                private partial IOptionalCache Cache { get; }

                public ISearch ReadSearch() => Search;
            }
            """;

        var ct = TestContext.Current.CancellationToken;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "SlotProbe",
            [
                CSharpSyntaxTree.ParseText(Prelude, parseOptions, cancellationToken: ct),
                CSharpSyntaxTree.ParseText(source, parseOptions, cancellationToken: ct),
            ],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(
            generators: [new SlotGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);

        driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics, ct);

        Assert.Empty(generatorDiagnostics);
        var errors = outputCompilation.GetDiagnostics(ct).Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.Empty(errors);

        using var pe = new MemoryStream();
        var emit = outputCompilation.Emit(pe, cancellationToken: ct);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        pe.Position = 0;

        var alc = new AssemblyLoadContext("SlotProbeAlc", isCollectible: true);
        try
        {
            var assembly = alc.LoadFromStream(pe);

            // Trigger module initializer by loading a type from the assembly and invoking the getter.
            var crmType = assembly.GetType("Sample.CrmNeuron", throwOnError: true)!;
            var crm = Activator.CreateInstance(crmType)!;
            _ = crmType.GetMethod("ReadSearch")!.Invoke(crm, null);

            var slotsProp = crmType.GetProperty("Slots")!.GetValue(crm)!;
            var lastSlot = (string?)slotsProp.GetType().GetProperty("LastSlot")!.GetValue(slotsProp);
            Assert.Equal("search", lastSlot);

            // Verify ComponentRegistry registration.
            var registryType = assembly.GetType("DigitalBrain.Components.ComponentRegistry", throwOnError: true)!;
            var registered = (System.Collections.IDictionary)registryType.GetField("All")!.GetValue(null)!;
            Assert.True(registered.Contains(crmType));
            var componentId = registered[crmType]!;
            Assert.Equal("app.crm", (string)componentId.GetType().GetProperty("Value")!.GetValue(componentId)!);

            // Verify ComponentManifests entry.
            var manifestsType = assembly.GetType("DigitalBrain.Components.ComponentManifests", throwOnError: true)!;
            var manifests = (System.Collections.IDictionary)manifestsType.GetField("All")!.GetValue(null)!;
            var manifest = Assert.Single(manifests.Values.Cast<object>());
            var slots = (Array)manifest.GetType().GetProperty("Slots")!.GetValue(manifest)!;
            Assert.Equal(3, slots.Length);

            var names = slots.Cast<object>().Select(s => (string)s.GetType().GetProperty("Name")!.GetValue(s)!).ToArray();
            Assert.Equal(["search", "customers", "cache"], names);

            var customersSlot = slots.Cast<object>().Single(s => (string)s.GetType().GetProperty("Name")!.GetValue(s)! == "customers");
            Assert.Equal("customer_profiles", (string?)customersSlot.GetType().GetProperty("Key")!.GetValue(customersSlot));
            Assert.True((bool)customersSlot.GetType().GetProperty("Required")!.GetValue(customersSlot)!);

            var cacheSlot = slots.Cast<object>().Single(s => (string)s.GetType().GetProperty("Name")!.GetValue(s)! == "cache");
            Assert.False((bool)cacheSlot.GetType().GetProperty("Required")!.GetValue(cacheSlot)!);
        }
        finally
        {
            alc.Unload();
        }
    }
}
