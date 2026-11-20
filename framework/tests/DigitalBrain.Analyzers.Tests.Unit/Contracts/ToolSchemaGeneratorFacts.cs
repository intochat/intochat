using System.Collections.Immutable;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain.Analyzers.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DigitalBrain.Analyzers.Tests.Unit.Contracts;

public sealed class ToolSchemaGeneratorFacts
{
    private sealed record SchemaSnapshot(
        string Contract,
        string Name,
        string Method,
        string Description,
        string ParametersJson,
        string? DefaultKey,
        string? KeyPattern,
        bool ReadOnly);

    private const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.ComponentModel;
        using System.Text.Json.Serialization;
        using System.Threading;
        using System.Threading.Tasks;
        using DigitalBrain.Contracts;

        """;

    private const string BasePrelude = Usings + """
        namespace DigitalBrain.Contracts
        {
            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum, Inherited = false)]
            public sealed class NeuronAliasAttribute(string alias) : Attribute
            {
                public string Alias { get; } = alias;
            }

            [AttributeUsage(AttributeTargets.Method, Inherited = false)]
            public sealed class ReadOnlyCallAttribute : Attribute;
        }
        """;

    private const string ToolSchemasPrelude = """
        namespace DigitalBrain.Contracts
        {
            public sealed record ToolSchema(
                string Contract,
                string Name,
                string Method,
                string Description,
                string ParametersJson,
                string? DefaultKey,
                string? KeyPattern,
                bool ReadOnly);

            public static class ToolSchemas
            {
                public static readonly System.Collections.Generic.List<ToolSchema> All = [];
                public static void Register(ToolSchema[] schemas) => All.AddRange(schemas);
            }
        }
        """;

    private const string FocusSource = """
        namespace Sample;

        [JsonConverter(typeof(JsonStringEnumConverter<FocusTone>))]
        public enum FocusTone { Info, Success }

        public sealed record FocusAction(string Kind, string Argument);

        [NeuronAlias("focus")]
        public interface IFocus
        {
            const string DefaultKey = "ino/focus";

            Task<string> Point(string target, FocusTone tone, string reason, FocusAction? action = null);
        }
        """;

    private static MetadataReference[] References =>
    [
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
        MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
        MetadataReference.CreateFromFile(Assembly.Load("System.Collections").Location),
        MetadataReference.CreateFromFile(typeof(DescriptionAttribute).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(JsonConverterAttribute).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Uri).Assembly.Location),
    ];

    private static (ImmutableArray<Diagnostic> GeneratorDiagnostics, ImmutableArray<Diagnostic> OutputDiagnostics, string[] GeneratedSources, SchemaSnapshot[] Schemas) Run(
        string source,
        bool includeToolSchemas = true)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = includeToolSchemas
            ? new[]
            {
                CSharpSyntaxTree.ParseText(BasePrelude, parseOptions),
                CSharpSyntaxTree.ParseText(ToolSchemasPrelude, parseOptions),
                CSharpSyntaxTree.ParseText(Usings + source, parseOptions),
            }
            : new[]
            {
                CSharpSyntaxTree.ParseText(BasePrelude, parseOptions),
                CSharpSyntaxTree.ParseText(Usings + source, parseOptions),
            };

        var compilation = CSharpCompilation.Create(
            "ToolSchemaProbe",
            trees,
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var ct = TestContext.Current.CancellationToken;
        var inputErrors = compilation.GetDiagnostics(ct).Where(static d => d.Severity == DiagnosticSeverity.Error).Select(static d => d.ToString()).ToArray();
        Assert.Empty(inputErrors);

        var driver = CSharpGeneratorDriver.Create([new ToolSchemaGenerator().AsSourceGenerator()], parseOptions: parseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics, ct);
        var result = driver.GetRunResult();
        var generatedSources = result.GeneratedTrees.Select(static tree => tree.GetText().ToString()).ToArray();
        var outputDiagnostics = outputCompilation.GetDiagnostics(ct);

        if (!includeToolSchemas || outputDiagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error))
        {
            return (generatorDiagnostics, outputDiagnostics, generatedSources, []);
        }

        using var pe = new MemoryStream();
        var emit = outputCompilation.Emit(pe, cancellationToken: ct);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        pe.Position = 0;

        var alc = new AssemblyLoadContext("ToolSchemaProbeAlc", isCollectible: true);
        try
        {
            var assembly = alc.LoadFromStream(pe);
            RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
            var registryType = assembly.GetType("DigitalBrain.Contracts.ToolSchemas", throwOnError: true)!;
            var all = (System.Collections.IEnumerable)registryType.GetField("All")!.GetValue(null)!;
            var snapshots = new List<SchemaSnapshot>();
            foreach (var item in all)
            {
                var type = item.GetType();
                snapshots.Add(new SchemaSnapshot(
                    (string)type.GetProperty("Contract")!.GetValue(item)!,
                    (string)type.GetProperty("Name")!.GetValue(item)!,
                    (string)type.GetProperty("Method")!.GetValue(item)!,
                    (string)type.GetProperty("Description")!.GetValue(item)!,
                    (string)type.GetProperty("ParametersJson")!.GetValue(item)!,
                    (string?)type.GetProperty("DefaultKey")!.GetValue(item),
                    (string?)type.GetProperty("KeyPattern")!.GetValue(item),
                    (bool)type.GetProperty("ReadOnly")!.GetValue(item)!));
            }
            return (generatorDiagnostics, outputDiagnostics, generatedSources, [.. snapshots]);
        }
        finally
        {
            alc.Unload();
        }
    }

    private static SchemaSnapshot[] Schemas(string source)
    {
        var (genDiag, outDiag, _, schemas) = Run(source);
        Assert.Empty(genDiag.Where(static d => d.Severity == DiagnosticSeverity.Error));
        Assert.Empty(outDiag.Where(static d => d.Severity == DiagnosticSeverity.Error));
        return schemas;
    }

    [Fact]
    public void PointBecomesFocusPointWithAStringEnumAndANestedObject()
    {
        var schema = Schemas(FocusSource).Single(static s => s.Name == "focus_point");
        using var json = JsonDocument.Parse(schema.ParametersJson);
        var p = json.RootElement.GetProperty("properties");
        Assert.Equal("string", p.GetProperty("tone").GetProperty("type").GetString());
        Assert.Equal(["Info", "Success"], p.GetProperty("tone").GetProperty("enum").EnumerateArray().Select(static e => e.GetString()));
        Assert.Equal("object", p.GetProperty("action").GetProperty("type").GetString());
        Assert.Equal(["kind", "argument"], p.GetProperty("action").GetProperty("properties").EnumerateObject().Select(static x => x.Name));
        Assert.Equal(["target", "tone", "reason"], json.RootElement.GetProperty("required").EnumerateArray().Select(static e => e.GetString()));
        Assert.Equal("ino/focus", schema.DefaultKey);
        Assert.Equal("Sample.IFocus", schema.Contract);
        Assert.Equal("Point", schema.Method);
    }

    [Fact]
    public void AContractWithoutDefaultKeyAsksForAKeyFirst()
    {
        var schema = Schemas("""
            [NeuronAlias("codeblock.run")]
            public interface ICodeBlockRun
            {
                const string KeyPattern = "codeblocks/{new short id}";
                Task<string> Start(string title, string source, string input);
            }
            """).Single();

        Assert.Equal("codeblock_run_start", schema.Name);
        Assert.Null(schema.DefaultKey);
        Assert.Equal("codeblocks/{new short id}", schema.KeyPattern);

        using var json = JsonDocument.Parse(schema.ParametersJson);
        var props = json.RootElement.GetProperty("properties").EnumerateObject().ToArray();
        Assert.Equal("key", props[0].Name);
        Assert.Equal("string", props[0].Value.GetProperty("type").GetString());
        Assert.Contains("codeblocks/{new short id}", props[0].Value.GetProperty("description").GetString(), StringComparison.Ordinal);

        var required = json.RootElement.GetProperty("required").EnumerateArray().Select(static e => e.GetString()).ToArray();
        Assert.Equal(["key", "title", "source", "input"], required);
    }

    [Fact]
    public void OverloadsShareOneToolWithTheUnionOfParameters()
    {
        var schemas = Schemas("""
            [NeuronAlias("studio")]
            public interface IStudio
            {
                const string DefaultKey = "ino/studio";
                Task<string> Make(string goal);
                Task<string> Make(string? alias, string? title, string goal);
            }
            """);

        var schema = Assert.Single(schemas);
        Assert.Equal("studio_make", schema.Name);

        using var json = JsonDocument.Parse(schema.ParametersJson);
        var propNames = json.RootElement.GetProperty("properties").EnumerateObject().Select(static p => p.Name).ToArray();
        Assert.Contains("goal", propNames);
        Assert.Contains("alias", propNames);
        Assert.Contains("title", propNames);
        Assert.Equal(["goal"], json.RootElement.GetProperty("required").EnumerateArray().Select(static e => e.GetString()));
    }

    [Fact]
    public void CancellationTokenIsNeverAParameter()
    {
        var schema = Schemas("""
            [NeuronAlias("studio")]
            public interface IStudio
            {
                const string DefaultKey = "ino/studio";
                Task<string> Repositories(CancellationToken ct = default);
            }
            """).Single();

        using var json = JsonDocument.Parse(schema.ParametersJson);
        Assert.Empty(json.RootElement.GetProperty("properties").EnumerateObject());
    }

    [Fact]
    public void ANameOver64CharactersIsDB0046()
    {
        var longAlias = new string('a', 60);
        var (genDiag, _, _, _) = Run($$"""
            [NeuronAlias("{{longAlias}}")]
            public interface ITooLong
            {
                const string DefaultKey = "long";
                Task GoNow();
            }
            """);

        var diagnostic = Assert.Single(genDiag, static d => d.Id == "DB0046");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void GeneratedSourceCompilesWithoutReflection()
    {
        var (genDiag, outDiag, sources, schemas) = Run(FocusSource);
        Assert.Empty(genDiag.Where(static d => d.Severity == DiagnosticSeverity.Error));
        Assert.Empty(outDiag.Where(static d => d.Severity == DiagnosticSeverity.Error));
        Assert.NotEmpty(schemas);
        var generated = Assert.Single(sources);
        Assert.DoesNotContain("typeof(", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("GetMethod", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void MethodMatchingAliasLastSegmentDropsDuplicateSuffix()
    {
        var schema = Schemas("""
            [NeuronAlias("web.search")]
            public interface IWebSearch
            {
                const string DefaultKey = "web/search";
                Task<string> Search(string query, int count = 8);
            }
            """).Single();

        Assert.Equal("web_search", schema.Name);
        using var json = JsonDocument.Parse(schema.ParametersJson);
        var p = json.RootElement.GetProperty("properties");
        Assert.Equal(8, p.GetProperty("count").GetProperty("default").GetInt32());
        Assert.Equal(["query"], json.RootElement.GetProperty("required").EnumerateArray().Select(static e => e.GetString()));
    }

    [Fact]
    public void DescriptionsTypesIntegerEnumsCollectionsCyclesAndReadOnlyMapCorrectly()
    {
        var schemas = Schemas("""
            public enum Priority { Low = 0, High = 2 }
            public sealed record Node(string Label, Node? Next);

            [NeuronAlias("sample.api")]
            public interface ISampleApi
            {
                const string DefaultKey = "sample/api";

                [Description("Explicit description wins.")]
                [ReadOnlyCall]
                Task<string> Inspect(
                    [Description("When it ran.")] DateTimeOffset at,
                    Uri endpoint,
                    Priority priority,
                    double weight,
                    bool dryRun,
                    string[] tags,
                    IReadOnlyList<int> counts,
                    Node tree);

                /// <summary>Reads <paramref name="id"/> from <c>store</c>.</summary>
                Task<string> FromSummary(string id);

                // Leading comment line one.
                // Leading comment line two.
                Task<string> FromComment();

                Task<string> Fallback();
            }
            """);

        var inspect = schemas.Single(static s => s.Method == "Inspect");
        Assert.True(inspect.ReadOnly);
        Assert.Equal("Explicit description wins.", inspect.Description);

        using var json = JsonDocument.Parse(inspect.ParametersJson);
        var p = json.RootElement.GetProperty("properties");
        Assert.Equal("date-time", p.GetProperty("at").GetProperty("format").GetString());
        Assert.Equal("When it ran.", p.GetProperty("at").GetProperty("description").GetString());
        Assert.Equal("uri", p.GetProperty("endpoint").GetProperty("format").GetString());
        Assert.Equal("integer", p.GetProperty("priority").GetProperty("type").GetString());
        Assert.Equal([0, 2], p.GetProperty("priority").GetProperty("enum").EnumerateArray().Select(static e => e.GetInt32()));
        Assert.Contains("Low", p.GetProperty("priority").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("High", p.GetProperty("priority").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal("number", p.GetProperty("weight").GetProperty("type").GetString());
        Assert.Equal("boolean", p.GetProperty("dryRun").GetProperty("type").GetString());
        Assert.Equal("array", p.GetProperty("tags").GetProperty("type").GetString());
        Assert.Equal("string", p.GetProperty("tags").GetProperty("items").GetProperty("type").GetString());
        Assert.Equal("array", p.GetProperty("counts").GetProperty("type").GetString());
        Assert.Equal("integer", p.GetProperty("counts").GetProperty("items").GetProperty("type").GetString());
        Assert.Equal("object", p.GetProperty("tree").GetProperty("type").GetString());
        Assert.Equal("object", p.GetProperty("tree").GetProperty("properties").GetProperty("next").GetProperty("type").GetString());
        Assert.False(p.GetProperty("tree").GetProperty("properties").GetProperty("next").TryGetProperty("properties", out _));

        Assert.Equal("Reads id from store.", schemas.Single(static s => s.Method == "FromSummary").Description);
        Assert.Equal("Leading comment line one. Leading comment line two.", schemas.Single(static s => s.Method == "FromComment").Description);
        Assert.Equal("Fallback on sample.api.", schemas.Single(static s => s.Method == "Fallback").Description);
    }

    [Fact]
    public void SkipsEmitWhenToolSchemasIsNotAccessible()
    {
        var (genDiag, outDiag, sources, _) = Run(FocusSource, includeToolSchemas: false);
        Assert.Empty(genDiag.Where(static d => d.Severity == DiagnosticSeverity.Error));
        Assert.Empty(outDiag.Where(static d => d.Severity == DiagnosticSeverity.Error));
        Assert.Empty(sources);
    }

    [Fact]
    public void NestedObjectStopsExpandingAtDepthFour()
    {
        var schema = Schemas("""
            public sealed record L5(string Leaf);
            public sealed record L4(L5 Five);
            public sealed record L3(L4 Four);
            public sealed record L2(L3 Three);
            public sealed record L1(L2 Two);

            [NeuronAlias("depth.probe")]
            public interface IDepthProbe
            {
                const string DefaultKey = "depth";
                Task Check(L1 one);
            }
            """).Single();

        using var json = JsonDocument.Parse(schema.ParametersJson);
        var l1 = json.RootElement.GetProperty("properties").GetProperty("one");
        var l2 = l1.GetProperty("properties").GetProperty("two");
        var l3 = l2.GetProperty("properties").GetProperty("three");
        var l4 = l3.GetProperty("properties").GetProperty("four");
        var l5 = l4.GetProperty("properties").GetProperty("five");
        Assert.Equal("object", l5.GetProperty("type").GetString());
        Assert.False(l5.TryGetProperty("properties", out _));
    }

    [Fact]
    public void IncrementalGenerationCachesUnchangedContracts()
    {
        var ct = TestContext.Current.CancellationToken;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "ToolSchemaCacheProbe",
            [
                CSharpSyntaxTree.ParseText(BasePrelude, parseOptions, cancellationToken: ct),
                CSharpSyntaxTree.ParseText(ToolSchemasPrelude, parseOptions, cancellationToken: ct),
                CSharpSyntaxTree.ParseText(Usings + FocusSource, parseOptions, cancellationToken: ct),
            ],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ToolSchemaGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation, ct);
        driver = driver.RunGenerators(compilation.Clone(), ct);
        var secondRun = driver.GetRunResult().Results.Single();
        Assert.All(
            secondRun.TrackedOutputSteps.SelectMany(static kv => kv.Value).SelectMany(static s => s.Outputs),
            output => Assert.Equal(IncrementalStepRunReason.Cached, output.Reason));
    }
}
