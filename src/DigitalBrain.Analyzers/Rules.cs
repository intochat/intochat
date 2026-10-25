using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace DigitalBrain.Analyzers;

// Every id here is listed in AnalyzerReleases.Unshipped.md (RS2008). Attributes are matched by full metadata name only.
internal static class Rules
{
    private const string Category = "DigitalBrain";

    public static readonly DiagnosticDescriptor ModuleShape = new(
        "DB0001", "Module class needs [Module] and a public static ModuleManifest Manifest",
        "'{0}' derives from DigitalBrain.Module and must {1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor SchedulingInApp = new(
        "DB0002", "Scheduling attribute inside an App class",
        "'{0}' is a neuron scheduling attribute and cannot be used in app '{1}'; put it on a [Neuron] class",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor OrleansOnIdentity = new(
        "DB0003", "Orleans attribute on a DigitalBrain identity type",
        "'{0}' carries the Orleans attribute '{1}'; Orleans attributes belong to the runtime, not to DigitalBrain types",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public const string ModuleAttribute = "DigitalBrain.ModuleAttribute";
    public const string AppAttribute = "DigitalBrain.AppAttribute";
    public const string ModuleType = "DigitalBrain.Module";
    public const string AppType = "DigitalBrain.App";
    public const string ManifestType = "DigitalBrain.ModuleManifest";

    public static readonly ImmutableHashSet<string> IdentityAttributes = ImmutableHashSet.Create(
        "DigitalBrain.NeuronAttribute", "DigitalBrain.SignalAttribute", AppAttribute, ModuleAttribute);

    public static readonly ImmutableHashSet<string> SchedulingAttributes = ImmutableHashSet.Create(
        "DigitalBrain.ReadOnlyCallAttribute", "DigitalBrain.OneWayCallAttribute", "DigitalBrain.InterleavesAttribute",
        "DigitalBrain.InterleavesWhenAttribute", "DigitalBrain.CallTimeoutAttribute", "DigitalBrain.ReentrantAttribute");

    public static string FullName(INamedTypeSymbol? type)
    {
        if (type is null) { return string.Empty; }
        return type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + type.MetadataName : type.MetadataName;
    }

    public static bool DerivesFrom(INamedTypeSymbol type, string fullName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (FullName(current) == fullName) { return true; }
        }
        return false;
    }

    public static bool Has(ISymbol symbol, string attribute)
    {
        foreach (var data in symbol.GetAttributes())
        {
            if (FullName(data.AttributeClass) == attribute) { return true; }
        }
        return false;
    }

    public static Location LocationOf(AttributeData data, ISymbol fallback)
        => data.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? fallback.Locations[0];

    public static bool IsOrleans(INamedTypeSymbol? attribute)
    {
        var ns = attribute?.ContainingNamespace;
        if (ns is null || ns.IsGlobalNamespace) { return false; }
        while (!ns.ContainingNamespace.IsGlobalNamespace) { ns = ns.ContainingNamespace; }
        return ns.Name == "Orleans";
    }
}
