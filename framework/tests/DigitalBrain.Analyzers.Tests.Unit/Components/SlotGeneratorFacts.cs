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
            public abstract record Signal;

            [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, Inherited = false)]
            public sealed class SlotAttribute : Attribute
            {
                public string? Name { get; init; }
                public string? Key { get; set; }
                public bool Required { get; set; } = true;
            }

            [AttributeUsage(AttributeTargets.Method, Inherited = false)]
            public sealed class OnAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.Class, Inherited = false)]
            public sealed class SignalAttribute(string alias) : Attribute
            {
                public string Alias { get; } = alias;
            }

            [AttributeUsage(AttributeTargets.Method, Inherited = false)]
            public sealed class SynapseAttribute : Attribute
            {
                public SynapseAttribute(Type from, string into) { Signal = from; State = into; }
                public SynapseAttribute(string signalAlias, string into) { SignalAlias = signalAlias; State = into; }
                public SynapseAttribute(string from) { State = from; }
                public Type? Signal { get; }
                public string? SignalAlias { get; }
                public string State { get; }
                public string? Every { get; init; }
            }
        }

        namespace DigitalBrain.Modules
        {
            [AttributeUsage(AttributeTargets.Class, Inherited = false)]
            public sealed class ModuleAttribute(string id) : Attribute
            {
                public string Id { get; } = id;
            }

            [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
            public sealed class ProvidesAttribute(params Type[] contracts) : Attribute
            {
                public Type[] Contracts { get; } = contracts;
            }
        }

        namespace DigitalBrain.Components
        {
            public enum ComponentKind { Module, App, Unit, Agent }

            public readonly record struct ComponentId(string Value);

            public sealed record SlotNeed(string Name, Type Contract, bool Required = true, string? Key = null);

            public sealed record ComponentFace(string Title, string Description = "", string? Icon = null, string? Category = null);

            public sealed record OperationSpec(string Name, string Description = "", Type? Input = null, string? ParametersJson = null, string? SignalAlias = null);

            public sealed record ComponentManifest(
                ComponentId Id,
                SlotNeed[] Slots,
                ComponentFace? Face = null,
                OperationSpec[]? Operations = null,
                ComponentKind Kind = ComponentKind.App,
                string Version = "1.0",
                Type[]? Provides = null,
                Type[]? Emits = null,
                Type[]? Reacts = null);

            public static class ComponentRegistry
            {
                public static readonly Dictionary<Type, ComponentId> All = new();
                public static void Register(Type neuronClass, ComponentId component) => All[neuronClass] = component;
            }

            public static class ComponentManifests
            {
                public static readonly Dictionary<ComponentId, ComponentManifest> All = new();
                public static void Register(ComponentManifest manifest) => All[manifest.Id] = manifest;
                public static void Register(Type neuronClass, ComponentManifest manifest) => All[manifest.Id] = manifest;
            }

            public sealed class Slots
            {
                public string? LastSlot { get; private set; }
                public bool LastWasTryGet { get; private set; }
                public T Get<T>(string slot) where T : class
                {
                    LastSlot = slot;
                    LastWasTryGet = false;
                    return default!;
                }
                public T? TryGet<T>(string slot) where T : class
                {
                    LastSlot = slot;
                    LastWasTryGet = true;
                    return default;
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

        namespace Sample.UI
        {
            [AttributeUsage(AttributeTargets.Class, Inherited = false)]
            public sealed class ScreenAttribute(string? alias = null) : Attribute
            {
                public string? Alias { get; } = alias;
                public string? Title { get; init; }
                public string? Icon { get; init; }
                public string? Category { get; set; }
            }
        }
        """;

    private static MetadataReference[] References =>
    [
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.ComponentModel.DescriptionAttribute).Assembly.Location),
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

    [Fact]
    public void AModuleWithoutSlotsGetsARegisteredManifest()
    {
        const string source = """
            using DigitalBrain.Modules;

            namespace Sample;

            public interface IWeatherService { }

            [Module("weather")]
            [Provides(typeof(IWeatherService))]
            public sealed class WeatherModule { }
            """;

        WithCompiledAssembly("ModuleProbe", source, assembly =>
        {
            var moduleType = assembly.GetType("Sample.WeatherModule", throwOnError: true)!;
            System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);

            var manifestsType = assembly.GetType("DigitalBrain.Components.ComponentManifests", throwOnError: true)!;
            var manifests = (System.Collections.IDictionary)manifestsType.GetField("All")!.GetValue(null)!;
            var manifest = Assert.Single(manifests.Values.Cast<object>());

            var id = manifest.GetType().GetProperty("Id")!.GetValue(manifest)!;
            Assert.Equal("module.weather", (string)id.GetType().GetProperty("Value")!.GetValue(id)!);

            var kind = manifest.GetType().GetProperty("Kind")!.GetValue(manifest)!;
            Assert.Equal("Module", kind.ToString());

            var provides = (Type[]?)manifest.GetType().GetProperty("Provides")!.GetValue(manifest);
            Assert.NotNull(provides);
            var weatherServiceType = assembly.GetType("Sample.IWeatherService", throwOnError: true)!;
            Assert.Equal([weatherServiceType], provides);

            var registryType = assembly.GetType("DigitalBrain.Components.ComponentRegistry", throwOnError: true)!;
            var registered = (System.Collections.IDictionary)registryType.GetField("All")!.GetValue(null)!;
            Assert.True(registered.Contains(moduleType));
        });
    }

    [Fact]
    public void AScreenWithoutSlotsGetsARegisteredManifest()
    {
        const string source = """
            using System.ComponentModel;
            using DigitalBrain;
            using DigitalBrain.Neurons;
            using Sample.UI;

            namespace Sample;

            [Signal("notes.save")]
            public sealed record SaveNote(string Body) : Signal;

            [Neuron("app.notes")]
            [Screen("app.notes", Title = "Notes", Icon = "file-text", Category = "Workspace")]
            [Description("Lightweight notes screen.")]
            public sealed class NotesScreen : Neuron
            {
                [On]
                [Description("Save a note.")]
                public void OnSave(SaveNote signal) { }
            }
            """;

        WithCompiledAssembly("ScreenProbe", source, assembly =>
        {
            var screenType = assembly.GetType("Sample.NotesScreen", throwOnError: true)!;
            System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);

            var registryType = assembly.GetType("DigitalBrain.Components.ComponentRegistry", throwOnError: true)!;
            var registered = (System.Collections.IDictionary)registryType.GetField("All")!.GetValue(null)!;
            Assert.True(registered.Contains(screenType));

            var manifestsType = assembly.GetType("DigitalBrain.Components.ComponentManifests", throwOnError: true)!;
            var manifests = (System.Collections.IDictionary)manifestsType.GetField("All")!.GetValue(null)!;
            var manifest = Assert.Single(manifests.Values.Cast<object>());

            var id = manifest.GetType().GetProperty("Id")!.GetValue(manifest)!;
            Assert.Equal("app.notes", (string)id.GetType().GetProperty("Value")!.GetValue(id)!);
            Assert.Equal("App", manifest.GetType().GetProperty("Kind")!.GetValue(manifest)!.ToString());

            var face = manifest.GetType().GetProperty("Face")!.GetValue(manifest);
            Assert.NotNull(face);
            Assert.Equal("Notes", (string)face!.GetType().GetProperty("Title")!.GetValue(face)!);
            Assert.Equal("Lightweight notes screen.", (string)face.GetType().GetProperty("Description")!.GetValue(face)!);
            Assert.Equal("file-text", (string?)face.GetType().GetProperty("Icon")!.GetValue(face));
            Assert.Equal("Workspace", (string?)face.GetType().GetProperty("Category")!.GetValue(face));

            var ops = (Array?)manifest.GetType().GetProperty("Operations")!.GetValue(manifest);
            Assert.NotNull(ops);
            var op = Assert.Single(ops!.Cast<object>());
            Assert.Equal("save", (string)op.GetType().GetProperty("Name")!.GetValue(op)!);
            Assert.Equal("notes.save", (string?)op.GetType().GetProperty("SignalAlias")!.GetValue(op));
        });
    }

    [Fact]
    public void ASynapseClassGetsARegisteredManifestWithReacts()
    {
        const string source = """
            using DigitalBrain;

            namespace Sample;

            [Signal("time.tick")]
            public sealed record Tick() : Signal;

            public static class WatcherUnit
            {
                [Synapse(typeof(Tick), "watch.web", Every = "every 5m")]
                public static void Poll(Tick tick) { }

                [Synapse("watch.web")]
                public static string Show() => "ok";
            }
            """;

        WithCompiledAssembly("SynapseProbe", source, assembly =>
        {
            var tickType = assembly.GetType("Sample.Tick", throwOnError: true)!;
            System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);

            var manifestsType = assembly.GetType("DigitalBrain.Components.ComponentManifests", throwOnError: true)!;
            var manifests = (System.Collections.IDictionary)manifestsType.GetField("All")!.GetValue(null)!;
            var manifest = Assert.Single(manifests.Values.Cast<object>());

            var id = manifest.GetType().GetProperty("Id")!.GetValue(manifest)!;
            Assert.Equal("unit.watch.web", (string)id.GetType().GetProperty("Value")!.GetValue(id)!);
            Assert.Equal("Unit", manifest.GetType().GetProperty("Kind")!.GetValue(manifest)!.ToString());

            var reacts = (Type[]?)manifest.GetType().GetProperty("Reacts")!.GetValue(manifest);
            Assert.NotNull(reacts);
            Assert.Equal([tickType], reacts);
        });
    }

    [Fact]
    public void NullableAndInheritedSlotsGenerateTryGetAndManifestEntries()
    {
        const string source = """
            #nullable enable
            using DigitalBrain;
            using DigitalBrain.Neurons;

            namespace Sample;

            public interface IOptionalStudio { }

            public abstract partial class StudioBase<TState> : Neuron where TState : class
            {
                [Slot(Key = "studio", Required = false)]
                protected partial IOptionalStudio? Studio { get; }

                public IOptionalStudio? ReadStudio() => Studio;
            }

            [Neuron("app.paint")]
            public sealed class PaintApp : StudioBase<object>
            {
            }
            """;

        WithCompiledAssembly("NullableInheritedProbe", source, assembly =>
        {
            var paintType = assembly.GetType("Sample.PaintApp", throwOnError: true)!;
            var studioIface = assembly.GetType("Sample.IOptionalStudio", throwOnError: true)!;
            var paint = Activator.CreateInstance(paintType)!;
            _ = paintType.GetMethod("ReadStudio")!.Invoke(paint, null);

            var slotsProp = paintType.GetProperty("Slots")!.GetValue(paint)!;
            Assert.Equal("studio", (string?)slotsProp.GetType().GetProperty("LastSlot")!.GetValue(slotsProp));
            Assert.True((bool)slotsProp.GetType().GetProperty("LastWasTryGet")!.GetValue(slotsProp)!);

            var manifestsType = assembly.GetType("DigitalBrain.Components.ComponentManifests", throwOnError: true)!;
            var manifests = (System.Collections.IDictionary)manifestsType.GetField("All")!.GetValue(null)!;
            var manifest = Assert.Single(manifests.Values.Cast<object>());
            var id = manifest.GetType().GetProperty("Id")!.GetValue(manifest)!;
            Assert.Equal("app.paint", (string)id.GetType().GetProperty("Value")!.GetValue(id)!);

            var slots = (Array)manifest.GetType().GetProperty("Slots")!.GetValue(manifest)!;
            var slot = Assert.Single(slots.Cast<object>());
            Assert.Equal("studio", (string)slot.GetType().GetProperty("Name")!.GetValue(slot)!);
            Assert.Equal(studioIface, (Type)slot.GetType().GetProperty("Contract")!.GetValue(slot)!);
            Assert.False((bool)slot.GetType().GetProperty("Required")!.GetValue(slot)!);
            Assert.Equal("studio", (string?)slot.GetType().GetProperty("Key")!.GetValue(slot));
        });
    }

    private static void WithCompiledAssembly(string assemblyName, string source, Action<Assembly> verify)
    {
        var ct = TestContext.Current.CancellationToken;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            assemblyName,
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

        var alc = new AssemblyLoadContext(assemblyName + "Alc", isCollectible: true);
        try
        {
            var assembly = alc.LoadFromStream(pe);
            verify(assembly);
        }
        finally
        {
            alc.Unload();
        }
    }
}
