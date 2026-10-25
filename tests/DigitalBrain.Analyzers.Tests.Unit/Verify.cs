using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.CSharp.Testing;

namespace DigitalBrain.Analyzers.Tests.Unit;

// Default verifier only (never Verifiers.XUnit). The DigitalBrain and Orleans attributes are stubs declared in
// test source, so the tests reference neither package; the analyzers match by full metadata name.
internal static class Verify<TAnalyzer> where TAnalyzer : DiagnosticAnalyzer, new()
{
    internal const string Stubs = """
        namespace DigitalBrain
        {
            [System.AttributeUsage(System.AttributeTargets.Class)] public sealed class NeuronAttribute(string type) : System.Attribute { public string Type { get; } = type; }
            [System.AttributeUsage(System.AttributeTargets.Class)] public sealed class SignalAttribute(string alias) : System.Attribute { public string Alias { get; } = alias; }
            [System.AttributeUsage(System.AttributeTargets.Class)] public sealed class AppAttribute(string id) : System.Attribute { public string Id { get; } = id; }
            [System.AttributeUsage(System.AttributeTargets.Class)] public sealed class ModuleAttribute(string id) : System.Attribute { public string Id { get; } = id; }
            public sealed class InterleavesAttribute : System.Attribute;
            public sealed class InterleavesWhenAttribute(string predicate) : System.Attribute { public string Predicate { get; } = predicate; }
            public sealed class ReadOnlyCallAttribute : System.Attribute;
            public sealed class OneWayCallAttribute : System.Attribute;
            public sealed class CallTimeoutAttribute(int milliseconds) : System.Attribute { public int Milliseconds { get; } = milliseconds; }
            public sealed class ReentrantAttribute : System.Attribute;
            public abstract class Module { }
            public abstract class App { }
            public abstract class Neuron { }
            public sealed class ModuleManifest { }
            public abstract record Signal;
            [System.AttributeUsage(System.AttributeTargets.Method)] public sealed class OnAttribute : System.Attribute;
        }
        namespace Orleans
        {
            public sealed class GenerateSerializerAttribute : System.Attribute;
        }
        """;

    internal static Task Diagnostics(string source)
    {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
            TestCode = source,
        };
        test.TestState.Sources.Add(("Stubs.cs", Stubs));
        return test.RunAsync(TestContext.Current.CancellationToken);
    }
}
