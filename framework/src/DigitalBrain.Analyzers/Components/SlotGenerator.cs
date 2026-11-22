using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DigitalBrain.Analyzers.Components;

/// <summary>
/// Generates partial property getters and <c>[ModuleInitializer]</c> manifest registrations for <c>[Slot]</c> properties on neurons.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class SlotGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var properties = context.SyntaxProvider.ForAttributeWithMetadataName(
                "DigitalBrain.SlotAttribute",
                predicate: static (node, _) => node is PropertyDeclarationSyntax,
                transform: static (ctx, ct) => ExtractProperty(ctx, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect();

        var registrationProbe = context.CompilationProvider.Select(static (comp, _) =>
        {
            var assemblyName = comp.AssemblyName ?? string.Empty;
            var registry = comp.GetTypeByMetadataName("DigitalBrain.Components.ComponentRegistry");
            var manifests = comp.GetTypeByMetadataName("DigitalBrain.Components.ComponentManifests");
            if (registry is null || manifests is null)
            {
                return (CanRegister: false, AssemblyName: assemblyName);
            }

            var regMethod = registry.GetMembers("Register").OfType<IMethodSymbol>().FirstOrDefault();
            var manMethod = manifests.GetMembers("Register").OfType<IMethodSymbol>().FirstOrDefault();
            var canRegister = regMethod is not null
                && manMethod is not null
                && comp.IsSymbolAccessibleWithin(regMethod, comp.Assembly)
                && comp.IsSymbolAccessibleWithin(manMethod, comp.Assembly);

            return (CanRegister: canRegister, AssemblyName: assemblyName);
        });

        context.RegisterSourceOutput(
            properties.Combine(registrationProbe),
            static (spc, pair) => Emit(spc, pair.Left, pair.Right.CanRegister, pair.Right.AssemblyName));
    }

    private static SlotPropertyModel? ExtractProperty(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.TargetSymbol is not IPropertySymbol property
            || property.IsStatic
            || property.ContainingType is not { } owner
            || owner.TypeKind != TypeKind.Class)
        {
            return null;
        }

        var syntax = context.TargetNode as PropertyDeclarationSyntax;
        var isPartial = syntax is not null && syntax.Modifiers.Any(SyntaxKind.PartialKeyword);

        string? explicitName = null;
        string? key = null;
        var required = true;

        foreach (var attribute in context.Attributes)
        {
            foreach (var named in attribute.NamedArguments)
            {
                if (string.Equals(named.Key, "Name", StringComparison.Ordinal) && named.Value.Value is string rawName && !string.IsNullOrWhiteSpace(rawName))
                {
                    explicitName = rawName;
                }
                else if (string.Equals(named.Key, "Key", StringComparison.Ordinal) && named.Value.Value is string rawKey && !string.IsNullOrWhiteSpace(rawKey))
                {
                    key = rawKey;
                }
                else if (string.Equals(named.Key, "Required", StringComparison.Ordinal) && named.Value.Value is bool rawRequired)
                {
                    required = rawRequired;
                }
            }
            break;
        }

        var slotName = explicitName ?? ToCamelCase(property.Name);
        var componentId = NeuronTypeOf(owner) ?? owner.ToDisplayString();
        var ns = owner.ContainingNamespace.IsGlobalNamespace ? string.Empty : owner.ContainingNamespace.ToDisplayString();

        var chain = new List<TypeDeclModel>();
        for (var current = owner; current is not null; current = current.ContainingType)
        {
            var kind = current.IsRecord ? "record class" : "class";
            chain.Add(new TypeDeclModel(kind, current.Name));
        }
        chain.Reverse();

        return new SlotPropertyModel(
            ns,
            chain.ToImmutableArray(),
            owner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            componentId,
            property.Name,
            AccessibilityKeyword(property.DeclaredAccessibility),
            property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            slotName,
            key,
            required,
            isPartial);
    }

    private static string? NeuronTypeOf(INamedTypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes())
        {
            var fullName = Rules.FullName(attribute.AttributeClass);
            if ((fullName == "DigitalBrain.Neurons.NeuronAttribute" || fullName == "DigitalBrain.NeuronAttribute")
                && attribute.ConstructorArguments.Length > 0
                && attribute.ConstructorArguments[0].Value is string raw
                && !string.IsNullOrWhiteSpace(raw))
            {
                return raw;
            }
        }
        return null;
    }

    private static string AccessibilityKeyword(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => "private",
    };

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) { return name; }
        if (char.IsLower(name[0])) { return name; }
        if (name.Length == 1) { return name.ToLowerInvariant(); }
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    private static void Emit(
        SourceProductionContext context,
        ImmutableArray<SlotPropertyModel> properties,
        bool canRegister,
        string assemblyName)
    {
        if (properties.IsDefaultOrEmpty)
        {
            return;
        }

        var groups = properties
            .GroupBy(static p => p.OwnerFullName, StringComparer.Ordinal)
            .OrderBy(static g => g.Key, StringComparer.Ordinal)
            .ToList();

        foreach (var group in groups)
        {
            var partialProps = group.Where(static p => p.IsPartial).ToList();
            if (partialProps.Count == 0)
            {
                continue;
            }

            var first = partialProps[0];
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#nullable enable");
            sb.AppendLine();

            var indent = string.Empty;
            if (first.Namespace.Length > 0)
            {
                sb.Append("namespace ").AppendLine(first.Namespace);
                sb.AppendLine("{");
                indent = "    ";
            }

            foreach (var decl in first.TypeChain)
            {
                sb.Append(indent).Append("partial ").Append(decl.Kind).Append(' ').AppendLine(decl.Name);
                sb.Append(indent).AppendLine("{");
                indent += "    ";
            }

            foreach (var prop in partialProps)
            {
                sb.Append(indent)
                    .Append(prop.Accessibility)
                    .Append(" partial ")
                    .Append(prop.ContractFullName)
                    .Append(' ')
                    .Append(prop.PropertyName)
                    .Append(" => Slots.Get<")
                    .Append(prop.ContractFullName)
                    .Append(">(")
                    .Append(CSharpLiteral(prop.SlotName))
                    .AppendLine(");");
            }

            for (var i = first.TypeChain.Length - 1; i >= 0; i--)
            {
                indent = indent.Substring(0, indent.Length - 4);
                sb.Append(indent).AppendLine("}");
            }

            if (first.Namespace.Length > 0)
            {
                sb.AppendLine("}");
            }

            var hintName = "DigitalBrain.Slots." + SanitizeIdentifier(first.OwnerFullName) + ".g.cs";
            context.AddSource(hintName, sb.ToString());
        }

        if (!canRegister)
        {
            return;
        }

        var initSb = new StringBuilder();
        var className = "DigitalBrain_ComponentSlots_" + SanitizeIdentifier(assemblyName);
        initSb.AppendLine("// <auto-generated/>");
        initSb.AppendLine("#nullable enable");
        initSb.AppendLine("using System.Runtime.CompilerServices;");
        initSb.AppendLine();
        initSb.AppendLine("[CompilerGenerated]");
        initSb.Append("internal static class ").AppendLine(className);
        initSb.AppendLine("{");
        initSb.AppendLine("    [ModuleInitializer]");
        initSb.AppendLine("    internal static void Initialize()");
        initSb.AppendLine("    {");

        foreach (var group in groups)
        {
            var items = group.ToList();
            var first = items[0];
            initSb.Append("        global::DigitalBrain.Components.ComponentRegistry.Register(typeof(")
                .Append(first.OwnerFullName)
                .Append("), new global::DigitalBrain.Components.ComponentId(")
                .Append(CSharpLiteral(first.ComponentId))
                .AppendLine("));");

            initSb.AppendLine("        global::DigitalBrain.Components.ComponentManifests.Register(");
            initSb.Append("            typeof(").Append(first.OwnerFullName).AppendLine("),");
            initSb.AppendLine("            new global::DigitalBrain.Components.ComponentManifest(");
            initSb.Append("                new global::DigitalBrain.Components.ComponentId(").Append(CSharpLiteral(first.ComponentId)).AppendLine("),");
            initSb.AppendLine("                new global::DigitalBrain.Components.SlotNeed[]");
            initSb.AppendLine("                {");
            foreach (var prop in items)
            {
                initSb.Append("                    new global::DigitalBrain.Components.SlotNeed(")
                    .Append(CSharpLiteral(prop.SlotName))
                    .Append(", typeof(")
                    .Append(prop.ContractFullName)
                    .Append("), ")
                    .Append(prop.Required ? "true" : "false")
                    .Append(", ")
                    .Append(prop.Key is null ? "null" : CSharpLiteral(prop.Key))
                    .AppendLine("),");
            }
            initSb.AppendLine("                }));");
        }

        initSb.AppendLine("    }");
        initSb.AppendLine("}");

        context.AddSource("DigitalBrain.ComponentSlots.g.cs", initSb.ToString());
    }

    private static string CSharpLiteral(string value)
        => SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value)).ToFullString();

    private static string SanitizeIdentifier(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "Assembly";
        }
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }
        if (sb.Length > 0 && char.IsDigit(sb[0]))
        {
            sb.Insert(0, '_');
        }
        return sb.ToString();
    }

    private sealed class TypeDeclModel : IEquatable<TypeDeclModel>
    {
        public TypeDeclModel(string kind, string name)
        {
            Kind = kind;
            Name = name;
        }

        public string Kind { get; }
        public string Name { get; }

        public bool Equals(TypeDeclModel? other)
            => other is not null
               && string.Equals(Kind, other.Kind, StringComparison.Ordinal)
               && string.Equals(Name, other.Name, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as TypeDeclModel);

        public override int GetHashCode()
        {
            unchecked
            {
                return (StringComparer.Ordinal.GetHashCode(Kind) * 397) ^ StringComparer.Ordinal.GetHashCode(Name);
            }
        }
    }

    private sealed class SlotPropertyModel : IEquatable<SlotPropertyModel>
    {
        public SlotPropertyModel(
            string ns,
            ImmutableArray<TypeDeclModel> typeChain,
            string ownerFullName,
            string componentId,
            string propertyName,
            string accessibility,
            string contractFullName,
            string slotName,
            string? key,
            bool required,
            bool isPartial)
        {
            Namespace = ns;
            TypeChain = typeChain;
            OwnerFullName = ownerFullName;
            ComponentId = componentId;
            PropertyName = propertyName;
            Accessibility = accessibility;
            ContractFullName = contractFullName;
            SlotName = slotName;
            Key = key;
            Required = required;
            IsPartial = isPartial;
        }

        public string Namespace { get; }
        public ImmutableArray<TypeDeclModel> TypeChain { get; }
        public string OwnerFullName { get; }
        public string ComponentId { get; }
        public string PropertyName { get; }
        public string Accessibility { get; }
        public string ContractFullName { get; }
        public string SlotName { get; }
        public string? Key { get; }
        public bool Required { get; }
        public bool IsPartial { get; }

        public bool Equals(SlotPropertyModel? other)
        {
            if (other is null
                || !string.Equals(Namespace, other.Namespace, StringComparison.Ordinal)
                || !string.Equals(OwnerFullName, other.OwnerFullName, StringComparison.Ordinal)
                || !string.Equals(ComponentId, other.ComponentId, StringComparison.Ordinal)
                || !string.Equals(PropertyName, other.PropertyName, StringComparison.Ordinal)
                || !string.Equals(Accessibility, other.Accessibility, StringComparison.Ordinal)
                || !string.Equals(ContractFullName, other.ContractFullName, StringComparison.Ordinal)
                || !string.Equals(SlotName, other.SlotName, StringComparison.Ordinal)
                || !string.Equals(Key, other.Key, StringComparison.Ordinal)
                || Required != other.Required
                || IsPartial != other.IsPartial
                || TypeChain.Length != other.TypeChain.Length)
            {
                return false;
            }
            for (var i = 0; i < TypeChain.Length; i++)
            {
                if (!TypeChain[i].Equals(other.TypeChain[i])) { return false; }
            }
            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as SlotPropertyModel);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(OwnerFullName);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(PropertyName);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(SlotName);
                return hash;
            }
        }
    }
}
