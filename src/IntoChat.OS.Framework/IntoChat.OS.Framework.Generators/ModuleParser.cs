using IntoChat.OS.Framework.Generators.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntoChat.OS.Framework.Generators;

// Turns attributed symbols into value-only models. Nothing returned here holds a symbol or a compilation.
internal static class ModuleParser
{
    private sealed record Draft(string Id, string Display, List<FieldModel> Secrets, List<FieldModel> Settings);

    public static ModuleModel Module(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        var symbol = (INamedTypeSymbol)context.TargetSymbol;
        var declaration = (ClassDeclarationSyntax)context.TargetNode;
        var diagnostics = new List<DiagnosticModel>();
        var ns = Namespace(symbol);

        if (!IsTopLevelPartial(symbol, declaration))
        {
            diagnostics.Add(Diagnostic("IOS0001", symbol.Name, declaration));
        }

        if (!DerivesFromModule(symbol))
        {
            diagnostics.Add(Diagnostic("IOS0004", symbol.Name, declaration));
        }

        var id = Arg(context.Attributes[0], 0) as string ?? "";
        if (!IsModuleId(id))
        {
            diagnostics.Add(Diagnostic("IOS0002", "The module id must be ASCII letters, digits and hyphens.", declaration));
        }

        if (diagnostics.Count > 0)
        {
            return Failed(ns, symbol.Name, id, declaration, diagnostics);
        }

        string? anchor = null;
        string? license = null;
        var integrations = new List<Draft>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in symbol.GetAttributes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (attribute.AttributeClass?.ToDisplayString())
            {
                case "IntoChat.OS.Framework.Modules.ModuleContractsAttribute":
                    if (Arg(attribute, 0) is ITypeSymbol type)
                    {
                        anchor = type.ToDisplayString(QualifiedName);
                    }
                    break;
                case "IntoChat.OS.Framework.Modules.RequiresLicenseAttribute":
                    if (Arg(attribute, 0) is string product) { license = product; }
                    break;
                case "IntoChat.OS.Framework.Modules.UsesIntegrationAttribute":
                    if (Arg(attribute, 0) is string usedId && IsIntegrationId(usedId)) { used.Add(usedId); }
                    else { diagnostics.Add(Diagnostic("IOS0002", "An integration id is lowercase letters, digits, dots and dashes and needs a display name.", declaration)); }
                    break;
                case "IntoChat.OS.Framework.Modules.IntegrationAttribute":
                    if (Arg(attribute, 0) is string integrationId && Arg(attribute, 1) is string display)
                    {
                        if (!IsIntegrationId(integrationId) || string.IsNullOrWhiteSpace(display))
                        {
                            diagnostics.Add(Diagnostic("IOS0002", "An integration id is lowercase letters, digits, dots and dashes and needs a display name.", declaration));
                        }
                        else if (integrations.Any(existing => existing.Id == integrationId))
                        {
                            diagnostics.Add(Diagnostic("IOS0002", "An integration is declared once per module.", declaration));
                        }
                        else
                        {
                            integrations.Add(new Draft(integrationId, display, [], []));
                        }
                    }
                    break;
            }
        }

        foreach (var attribute in symbol.GetAttributes())
        {
            var kind = attribute.AttributeClass?.ToDisplayString();
            var secret = kind == "IntoChat.OS.Framework.Modules.SecretAttribute";
            if (!secret && kind != "IntoChat.OS.Framework.Modules.SettingAttribute") { continue; }

            string? target = null;
            var optional = false;
            foreach (var pair in attribute.NamedArguments)
            {
                if (pair.Key == "Integration") { target = pair.Value.Value as string; }
                else if (pair.Key == "Optional") { optional = pair.Value.Value is true; }
            }

            if (Arg(attribute, 0) is not string name || !IsFieldName(name))
            {
                diagnostics.Add(Diagnostic("IOS0002", "A field name is letters and digits, starting with a letter.", declaration));
                continue;
            }

            Draft? owner;
            if (target is not null)
            {
                owner = integrations.FirstOrDefault(candidate => candidate.Id == target);
                if (owner is null)
                {
                    diagnostics.Add(Diagnostic("IOS0002", "A field names an integration the module does not declare.", declaration));
                    continue;
                }
            }
            else if (integrations.Count == 1)
            {
                owner = integrations[0];
            }
            else
            {
                diagnostics.Add(Diagnostic("IOS0003", "Name the integration of each field when the module declares other than exactly one integration.", declaration));
                continue;
            }

            if (owner.Secrets.Concat(owner.Settings).Any(existing => existing.Name == name))
            {
                diagnostics.Add(Diagnostic("IOS0002", "A field is declared once per integration.", declaration));
                continue;
            }

            (secret ? owner.Secrets : owner.Settings).Add(new FieldModel(name, optional));
        }

        string? priceList = null;
        var prices = symbol.GetAttributes().Where(attribute => attribute.AttributeClass?.ToDisplayString() == "IntoChat.OS.Framework.Modules.ModulePriceListAttribute").ToArray();
        if (prices.Length > 1)
        {
            diagnostics.Add(Diagnostic("IOS0006", "Declare one price list per module.", declaration));
        }
        else if (prices.Length == 1)
        {
            var contract = context.SemanticModel.Compilation.GetTypeByMetadataName("IntoChat.OS.Contracts.Compute.IPriceList");
            if (Arg(prices[0], 0) is not INamedTypeSymbol priceType || priceType.TypeKind != TypeKind.Class || priceType.IsAbstract
                || priceType.IsUnboundGenericType || HasTypeParameters(priceType)
                || !context.SemanticModel.Compilation.IsSymbolAccessibleWithin(priceType, symbol)
                || contract is null || !priceType.AllInterfaces.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, contract)))
            {
                diagnostics.Add(Diagnostic("IOS0005", "The price list must be an accessible, concrete, closed class implementing IPriceList.", declaration));
            }
            else { priceList = priceType.ToDisplayString(QualifiedName); }
        }

        var operations = new List<ExternalOperationModel>();
        foreach (var attribute in symbol.GetAttributes().Where(attribute => attribute.AttributeClass?.ToDisplayString() == "IntoChat.OS.Framework.Modules.ExternalOperationAttribute"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Arg(attribute, 0) as string ?? "";
            var integration = Arg(attribute, 1) as string ?? "";
            var unit = Arg(attribute, 2) as string ?? "";
            if (prices.Length == 0 || !IsCanonicalToken(name) || !IsCanonicalToken(unit) || !(integrations.Any(candidate => candidate.Id == integration) || used.Contains(integration)))
            {
                diagnostics.Add(Diagnostic("IOS0007", "An external operation needs a module price list, canonical name and unit, and a declared integration.", declaration));
            }
            else if (operations.Any(operation => operation.Name == name))
            {
                diagnostics.Add(Diagnostic("IOS0008", "Declare an external operation once per module, with one canonical unit.", declaration));
            }
            else { operations.Add(new(name, integration, unit)); }
        }

        if (diagnostics.Count > 0)
        {
            return Failed(ns, symbol.Name, id, declaration, diagnostics);
        }

        return new ModuleModel(
            ns,
            symbol.Name,
            id,
            anchor,
            license,
            new EquatableArray<IntegrationModel>([.. integrations.Select(i => new IntegrationModel(
                i.Id, i.Display, new EquatableArray<FieldModel>([.. i.Secrets]), new EquatableArray<FieldModel>([.. i.Settings])))]),
            priceList,
            new EquatableArray<ExternalOperationModel>([.. operations.OrderBy(operation => operation.Name, StringComparer.Ordinal)]),
            Declaration(declaration),
            new EquatableArray<DiagnosticModel>([]));
    }

    public static HostingModel Hosting(GeneratorAttributeSyntaxContext context)
    {
        var symbol = (INamedTypeSymbol)context.TargetSymbol;
        var declaration = (ClassDeclarationSyntax)context.TargetNode;
        var diagnostics = new List<DiagnosticModel>();
        if (!IsTopLevelPartial(symbol, declaration))
        {
            diagnostics.Add(Diagnostic("IOS0001", symbol.Name, declaration));
        }

        var module = "";
        if (Arg(context.Attributes[0], 0) is INamedTypeSymbol type && DerivesFromModule(type))
        {
            module = type.ToDisplayString(QualifiedName);
        }
        else
        {
            diagnostics.Add(Diagnostic("IOS0004", symbol.Name, declaration));
        }

        return new HostingModel(Namespace(symbol), symbol.Name, module, new EquatableArray<DiagnosticModel>([.. diagnostics]));
    }

    // Fully qualified display. Qualification.For shortens a name the target namespace would capture.
    internal static readonly SymbolDisplayFormat QualifiedName = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static object? Arg(AttributeData attribute, int index)
        => attribute.ConstructorArguments.Length > index ? attribute.ConstructorArguments[index].Value : null;

    private static ModuleModel Failed(string? ns, string name, string id, ClassDeclarationSyntax declaration, List<DiagnosticModel> diagnostics)
        => new(ns, name, id, null, null, new EquatableArray<IntegrationModel>([]), null,
            new EquatableArray<ExternalOperationModel>([]), Declaration(declaration), new EquatableArray<DiagnosticModel>([.. diagnostics]));

    private static DeclarationModel Declaration(ClassDeclarationSyntax declaration)
    {
        var location = declaration.Identifier.GetLocation();
        var start = location.GetLineSpan().StartLinePosition;
        return new(location.SourceTree?.FilePath ?? "", location.SourceSpan.Start, location.SourceSpan.Length, start.Line, start.Character);
    }

    private static bool HasTypeParameters(INamedTypeSymbol type)
        => type.TypeArguments.Any(argument => argument.TypeKind == TypeKind.TypeParameter)
            || type.ContainingType is { } parent && HasTypeParameters(parent);

    private static bool IsCanonicalToken(string value)
        => value.Length is > 0 and <= 128 && value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
            or >= '0' and <= '9' or '.' or '_' or '-' or '/');

    private static string? Namespace(INamedTypeSymbol symbol)
        => symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null;

    private static bool IsTopLevelPartial(INamedTypeSymbol symbol, ClassDeclarationSyntax declaration)
        => symbol.ContainingType is null && declaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword));

    private static bool DerivesFromModule(INamedTypeSymbol symbol)
    {
        for (var type = symbol.BaseType; type is not null; type = type.BaseType)
        {
            if (type.OriginalDefinition.Name == "Module" && type.ContainingNamespace.ToDisplayString() == "IntoChat.OS.Framework.Modules")
            {
                return true;
            }
        }
        return false;
    }

    private static DiagnosticModel Diagnostic(string id, string argument, ClassDeclarationSyntax declaration)
    {
        var location = declaration.Identifier.GetLocation();
        var span = location.GetLineSpan();
        return new DiagnosticModel(id, argument, location.SourceTree?.FilePath ?? "", location.SourceSpan.Start, location.SourceSpan.Length,
            span.StartLinePosition.Line, span.StartLinePosition.Character);
    }

    private static bool IsModuleId(string value)
        => value.Length > 0 && value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-');

    private static bool IsIntegrationId(string value)
        => value.Length > 0 && value[0] is >= 'a' and <= 'z' && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');

    private static bool IsFieldName(string value)
        => value.Length > 0 && (value[0] is >= 'a' and <= 'z' or >= 'A' and <= 'Z') && value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9');
}
