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
        var face = ExtractFace(owner);
        var operations = ExtractOperations(owner);
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
            face,
            operations,
            property.Name,
            AccessibilityKeyword(property.DeclaredAccessibility),
            property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            slotName,
            key,
            required,
            isPartial);
    }

    private static ComponentFaceModel? ExtractFace(INamedTypeSymbol type)
    {
        AttributeData? screenAttr = null;
        string? classDescription = null;

        foreach (var attribute in type.GetAttributes())
        {
            var fullName = Rules.FullName(attribute.AttributeClass);
            if (attribute.AttributeClass?.Name == "ScreenAttribute")
            {
                screenAttr = attribute;
            }
            else if (fullName == "System.ComponentModel.DescriptionAttribute"
                && attribute.ConstructorArguments.Length > 0
                && attribute.ConstructorArguments[0].Value is string desc)
            {
                classDescription = desc;
            }
        }

        if (screenAttr is null)
        {
            return null;
        }

        string? title = null;
        string? description = null;
        string? icon = null;
        string? category = null;

        foreach (var named in screenAttr.NamedArguments)
        {
            if (string.Equals(named.Key, "Title", StringComparison.Ordinal) && named.Value.Value is string rawTitle)
            {
                title = rawTitle;
            }
            else if (string.Equals(named.Key, "Description", StringComparison.Ordinal) && named.Value.Value is string rawDesc)
            {
                description = rawDesc;
            }
            else if (string.Equals(named.Key, "Icon", StringComparison.Ordinal) && named.Value.Value is string rawIcon)
            {
                icon = rawIcon;
            }
            else if (string.Equals(named.Key, "Category", StringComparison.Ordinal) && named.Value.Value is string rawCat)
            {
                category = rawCat;
            }
        }

        return new ComponentFaceModel(
            string.IsNullOrWhiteSpace(title) ? type.Name : title!,
            description ?? classDescription ?? string.Empty,
            string.IsNullOrWhiteSpace(icon) ? null : icon,
            string.IsNullOrWhiteSpace(category) ? null : category);
    }

    private static ImmutableArray<OperationModel> ExtractOperations(INamedTypeSymbol type)
    {
        var ops = ImmutableArray.CreateBuilder<OperationModel>();
        foreach (var member in type.GetMembers())
        {
            if (member is not IMethodSymbol method
                || method.IsStatic
                || method.MethodKind != MethodKind.Ordinary
                || method.Parameters.Length == 0)
            {
                continue;
            }

            var hasOn = false;
            var description = string.Empty;
            foreach (var attribute in method.GetAttributes())
            {
                var fullName = Rules.FullName(attribute.AttributeClass);
                if (fullName == "DigitalBrain.OnAttribute" || fullName == "DigitalBrain.Neurons.OnAttribute")
                {
                    hasOn = true;
                }
                else if (fullName == "System.ComponentModel.DescriptionAttribute"
                    && attribute.ConstructorArguments.Length > 0
                    && attribute.ConstructorArguments[0].Value is string rawDesc)
                {
                    description = rawDesc;
                }
            }

            if (!hasOn)
            {
                continue;
            }

            var inputParam = method.Parameters[0].Type;
            var signalAlias = SignalAliasOf(inputParam);
            var opName = OperationNameOf(method, signalAlias);
            var parametersJson = Contracts.ToolSchemaGenerator.BuildInputParametersJson(inputParam);
            ops.Add(new OperationModel(
                opName,
                description,
                inputParam.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                parametersJson,
                signalAlias));
        }

        return ops.ToImmutable();
    }

    private static string? SignalAliasOf(ITypeSymbol inputType)
    {
        foreach (var attribute in inputType.GetAttributes())
        {
            var fullName = Rules.FullName(attribute.AttributeClass);
            if ((fullName == "DigitalBrain.SignalAttribute" || fullName == "DigitalBrain.Signals.SignalAttribute")
                && attribute.ConstructorArguments.Length > 0
                && attribute.ConstructorArguments[0].Value is string alias
                && !string.IsNullOrWhiteSpace(alias))
            {
                return alias;
            }
        }
        return null;
    }

    private static string OperationNameOf(IMethodSymbol method, string? signalAlias)
    {
        if (!string.IsNullOrWhiteSpace(signalAlias))
        {
            var dot = signalAlias!.LastIndexOf('.');
            if (dot >= 0 && dot + 1 < signalAlias.Length)
            {
                return signalAlias.Substring(dot + 1);
            }
            return signalAlias;
        }
        return ToCamelCase(method.Name);
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
            if (first.Face is null && first.Operations.Length == 0)
            {
                initSb.AppendLine("                }));");
            }
            else
            {
                initSb.AppendLine("                },");
                if (first.Face is { } face)
                {
                    initSb.Append("                new global::DigitalBrain.Components.ComponentFace(")
                        .Append(CSharpLiteral(face.Title))
                        .Append(", ")
                        .Append(CSharpLiteral(face.Description))
                        .Append(", ")
                        .Append(face.Icon is null ? "null" : CSharpLiteral(face.Icon))
                        .Append(", ")
                        .Append(face.Category is null ? "null" : CSharpLiteral(face.Category))
                        .AppendLine("),");
                }
                else
                {
                    initSb.AppendLine("                null,");
                }
                if (first.Operations.Length > 0)
                {
                    initSb.AppendLine("                new global::DigitalBrain.Components.OperationSpec[]");
                    initSb.AppendLine("                {");
                    foreach (var op in first.Operations)
                    {
                        initSb.Append("                    new global::DigitalBrain.Components.OperationSpec(")
                            .Append(CSharpLiteral(op.Name))
                            .Append(", ")
                            .Append(CSharpLiteral(op.Description))
                            .Append(", typeof(")
                            .Append(op.InputFullName)
                            .Append("), ")
                            .Append(CSharpLiteral(op.ParametersJson))
                            .Append(", ")
                            .Append(op.SignalAlias is null ? "null" : CSharpLiteral(op.SignalAlias))
                            .AppendLine("),");
                    }
                    initSb.AppendLine("                }));");
                }
                else
                {
                    initSb.AppendLine("                null));");
                }
            }
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

    private sealed class ComponentFaceModel : IEquatable<ComponentFaceModel>
    {
        public ComponentFaceModel(string title, string description, string? icon, string? category)
        {
            Title = title;
            Description = description;
            Icon = icon;
            Category = category;
        }

        public string Title { get; }
        public string Description { get; }
        public string? Icon { get; }
        public string? Category { get; }

        public bool Equals(ComponentFaceModel? other)
            => other is not null
               && string.Equals(Title, other.Title, StringComparison.Ordinal)
               && string.Equals(Description, other.Description, StringComparison.Ordinal)
               && string.Equals(Icon, other.Icon, StringComparison.Ordinal)
               && string.Equals(Category, other.Category, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as ComponentFaceModel);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(Title);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(Description);
                hash = (hash * 397) ^ (Icon is null ? 0 : StringComparer.Ordinal.GetHashCode(Icon));
                hash = (hash * 397) ^ (Category is null ? 0 : StringComparer.Ordinal.GetHashCode(Category));
                return hash;
            }
        }
    }

    private sealed class OperationModel : IEquatable<OperationModel>
    {
        public OperationModel(string name, string description, string inputFullName, string parametersJson, string? signalAlias)
        {
            Name = name;
            Description = description;
            InputFullName = inputFullName;
            ParametersJson = parametersJson;
            SignalAlias = signalAlias;
        }

        public string Name { get; }
        public string Description { get; }
        public string InputFullName { get; }
        public string ParametersJson { get; }
        public string? SignalAlias { get; }

        public bool Equals(OperationModel? other)
            => other is not null
               && string.Equals(Name, other.Name, StringComparison.Ordinal)
               && string.Equals(Description, other.Description, StringComparison.Ordinal)
               && string.Equals(InputFullName, other.InputFullName, StringComparison.Ordinal)
               && string.Equals(ParametersJson, other.ParametersJson, StringComparison.Ordinal)
               && string.Equals(SignalAlias, other.SignalAlias, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as OperationModel);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(Name);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(Description);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(InputFullName);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(ParametersJson);
                hash = (hash * 397) ^ (SignalAlias is null ? 0 : StringComparer.Ordinal.GetHashCode(SignalAlias));
                return hash;
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
            ComponentFaceModel? face,
            ImmutableArray<OperationModel> operations,
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
            Face = face;
            Operations = operations;
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
        public ComponentFaceModel? Face { get; }
        public ImmutableArray<OperationModel> Operations { get; }
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
                || !Equals(Face, other.Face)
                || Operations.Length != other.Operations.Length
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
            for (var i = 0; i < Operations.Length; i++)
            {
                if (!Operations[i].Equals(other.Operations[i])) { return false; }
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
                hash = (hash * 397) ^ (Face?.GetHashCode() ?? 0);
                hash = (hash * 397) ^ Operations.Length;
                return hash;
            }
        }
    }
}
