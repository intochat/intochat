using Microsoft.CodeAnalysis;

namespace IntoChat.OS.Framework.Generators;

// Every id here is listed in AnalyzerReleases.Unshipped.md (RS2008 enforces it). Messages carry names, never values.
internal static class Diagnostics
{
    private const string Category = "IntoChat.OS.Framework.Modules";

    public static readonly DiagnosticDescriptor NotPartial = new(
        "IOS0001", "Module type must be a top-level partial class",
        "'{0}' must be a top-level partial class so the generator can add its members",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidDeclaration = new(
        "IOS0002", "Invalid module or integration declaration",
        "{0}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingIntegration = new(
        "IOS0003", "Secret or setting does not name its integration",
        "{0}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NotAModule = new(
        "IOS0004", "Attribute target is not a module type",
        "'{0}' must derive from Module to carry this attribute",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidPriceList = new(
        "IOS0005", "Invalid module price-list type", "{0}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicatePriceList = new(
        "IOS0006", "Duplicate module price-list declaration", "{0}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidExternalOperation = new(
        "IOS0007", "Invalid external operation declaration", "{0}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateExternalOperation = new(
        "IOS0008", "Duplicate external operation declaration", "{0}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ConflictingExternalOwner = new(
        "IOS0009", "Conflicting external operation ownership", "{0}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static DiagnosticDescriptor ById(string id) => id switch
    {
        "IOS0001" => NotPartial,
        "IOS0002" => InvalidDeclaration,
        "IOS0003" => MissingIntegration,
        "IOS0005" => InvalidPriceList,
        "IOS0006" => DuplicatePriceList,
        "IOS0007" => InvalidExternalOperation,
        "IOS0008" => DuplicateExternalOperation,
        "IOS0009" => ConflictingExternalOwner,
        _ => NotAModule,
    };
}
