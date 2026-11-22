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
/// Generates partial property getters for <c>[Slot]</c> properties and <c>[ModuleInitializer]</c> manifest registrations
/// for <c>[Slot]</c> neurons, <c>[Module]</c> classes, <c>[Screen]</c> classes, and <c>[Synapse]</c> units.
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

        var modulesByModulesNs = context.SyntaxProvider.ForAttributeWithMetadataName(
                "DigitalBrain.Modules.ModuleAttribute",
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => ExtractModule(ctx, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect();

        var modulesByRootNs = context.SyntaxProvider.ForAttributeWithMetadataName(
                "DigitalBrain.ModuleAttribute",
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => ExtractModule(ctx, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect();

        var screens = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: static (ctx, ct) => ExtractScreen(ctx, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect();

        var synapses = context.SyntaxProvider.ForAttributeWithMetadataName(
                "DigitalBrain.SynapseAttribute",
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, ct) => ExtractSynapse(ctx, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect();

        var registrationProbe = context.CompilationProvider.Select(static (comp, _) =>
        {
            var assemblyName = comp.AssemblyName ?? string.Empty;
            var registry = comp.GetTypeByMetadataName("DigitalBrain.Components.ComponentRegistry");
            var manifests = comp.GetTypeByMetadataName("DigitalBrain.Components.ComponentManifests");
            var canRegister = false;
            if (registry is not null && manifests is not null)
            {
                var regMethod = registry.GetMembers("Register").OfType<IMethodSymbol>().FirstOrDefault();
                var manMethod = manifests.GetMembers("Register").OfType<IMethodSymbol>().FirstOrDefault();
                canRegister = regMethod is not null
                    && manMethod is not null
                    && comp.IsSymbolAccessibleWithin(regMethod, comp.Assembly)
                    && comp.IsSymbolAccessibleWithin(manMethod, comp.Assembly);
            }

            var screenSignals = comp.GetTypeByMetadataName("DigitalBrain.Kernel.ScreenSignals");
            var screenSignalHandler = comp.GetTypeByMetadataName("DigitalBrain.Kernel.ScreenSignalHandler");
            var screenRegisterMethod = screenSignals?.GetMembers("Register").OfType<IMethodSymbol>().FirstOrDefault();
            var canRegisterScreenSignals = screenRegisterMethod is not null
                && screenSignalHandler is not null
                && comp.IsSymbolAccessibleWithin(screenRegisterMethod, comp.Assembly)
                && comp.IsSymbolAccessibleWithin(screenSignalHandler, comp.Assembly);

            return (CanRegister: canRegister, CanRegisterScreenSignals: canRegisterScreenSignals, AssemblyName: assemblyName);
        });

        var combined = properties
            .Combine(modulesByModulesNs)
            .Combine(modulesByRootNs)
            .Combine(screens)
            .Combine(synapses)
            .Combine(registrationProbe);

        context.RegisterSourceOutput(
            combined,
            static (spc, data) =>
            {
                var (((((props, mods1), mods2), scrs), syns), probe) = data;
                Emit(spc, props, mods1, mods2, scrs, syns, probe.CanRegister, probe.CanRegisterScreenSignals, probe.AssemblyName);
            });
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
        var componentId = NeuronTypeOf(owner) ?? ScreenAliasOf(owner) ?? owner.ToDisplayString();
        var face = ExtractFace(owner);
        var operations = ExtractOperations(owner);
        var ns = owner.ContainingNamespace.IsGlobalNamespace ? string.Empty : owner.ContainingNamespace.ToDisplayString();

        var chain = new List<TypeDeclModel>();
        for (var current = owner; current is not null; current = current.ContainingType)
        {
            var kind = current.IsRecord ? "record class" : "class";
            var declName = current.IsGenericType
                ? current.Name + "<" + string.Join(", ", current.TypeParameters.Select(static tp => tp.Name)) + ">"
                : current.Name;
            chain.Add(new TypeDeclModel(kind, declName));
        }
        chain.Reverse();

        var isNullable = property.Type.NullableAnnotation == NullableAnnotation.Annotated;
        var rawType = property.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        var isCollection = TryUnwrapSlotCollection(rawType, out var contractType);
        var isAccessible = IsAccessibleOwner(owner) && !owner.IsStatic && IsAccessibleForTypeOf(contractType);

        return new SlotPropertyModel(
            ns,
            chain.ToImmutableArray(),
            owner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            componentId,
            face,
            operations,
            property.Name,
            AccessibilityKeyword(property.DeclaredAccessibility),
            contractType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            slotName,
            key,
            required,
            isNullable,
            isPartial,
            isAccessible,
            isCollection);
    }

    private static bool TryUnwrapSlotCollection(ITypeSymbol type, out ITypeSymbol contractType)
    {
        if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named
            && named.Name == "SlotCollection"
            && named.ContainingNamespace is { IsGlobalNamespace: false } ns
            && ns.ToDisplayString() == "DigitalBrain.Components")
        {
            contractType = named.TypeArguments[0].WithNullableAnnotation(NullableAnnotation.NotAnnotated);
            return true;
        }
        contractType = type;
        return false;
    }

    private static ComponentEntryModel? ExtractModule(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.TargetSymbol is not INamedTypeSymbol owner
            || owner.IsStatic
            || !IsAccessibleOwner(owner))
        {
            return null;
        }

        string? rawId = null;
        foreach (var attribute in context.Attributes)
        {
            if (attribute.ConstructorArguments.Length > 0
                && attribute.ConstructorArguments[0].Value is string id
                && !string.IsNullOrWhiteSpace(id))
            {
                rawId = id.Trim();
                break;
            }
        }

        var baseId = string.IsNullOrWhiteSpace(rawId) ? ToCamelCase(owner.Name) : rawId!;
        var componentId = baseId.StartsWith("module.", StringComparison.Ordinal) ? baseId : "module." + baseId;
        var provides = ExtractProvides(owner);
        var reacts = ExtractReacts(owner);

        return new ComponentEntryModel(
            owner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            componentId,
            "Module",
            isStatic: false,
            face: null,
            operations: ImmutableArray<OperationModel>.Empty,
            provides: provides,
            inheritedSlots: ImmutableArray<SlotPropertyModel>.Empty,
            reacts: reacts,
            signalHandlers: ImmutableArray<ScreenSignalHandlerModel>.Empty);
    }

    private static ComponentEntryModel? ExtractScreen(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Node is not ClassDeclarationSyntax classDecl)
        {
            return null;
        }

        if (context.SemanticModel.GetDeclaredSymbol(classDecl, cancellationToken) is not INamedTypeSymbol owner
            || owner.IsStatic
            || !IsAccessibleOwner(owner))
        {
            return null;
        }

        var face = ExtractFace(owner);
        var inheritedSlots = ExtractInheritedSlots(owner);
        var reacts = ExtractReacts(owner);
        var neuronType = NeuronTypeOf(owner);
        if (face is null && inheritedSlots.IsEmpty && (neuronType is null || reacts.IsEmpty))
        {
            return null;
        }

        var componentId = neuronType ?? ScreenAliasOf(owner) ?? owner.ToDisplayString();
        var operations = ExtractOperations(owner);
        var signalHandlers = face is not null || DerivesFromScreen(owner)
            ? ExtractScreenSignalHandlers(owner, context.SemanticModel.Compilation)
            : ImmutableArray<ScreenSignalHandlerModel>.Empty;

        return new ComponentEntryModel(
            owner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            componentId,
            "App",
            isStatic: false,
            face: face,
            operations: operations,
            provides: ImmutableArray<string>.Empty,
            inheritedSlots: inheritedSlots,
            reacts: reacts,
            signalHandlers: signalHandlers);
    }

    private static bool DerivesFromScreen(INamedTypeSymbol owner)
    {
        for (var current = owner.BaseType; current is not null; current = current.BaseType)
        {
            if (current.Name == "Screen"
                && current.ContainingNamespace is { IsGlobalNamespace: false } ns
                && ns.ToDisplayString() == "DigitalBrain")
            {
                return true;
            }
        }
        return false;
    }

    private static bool DerivesFromSignal(ITypeSymbol type)
    {
        for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
        {
            if (Rules.FullName(current) == "DigitalBrain.Signal")
            {
                return true;
            }
        }
        return false;
    }

    private static ImmutableArray<ScreenSignalHandlerModel> ExtractScreenSignalHandlers(INamedTypeSymbol owner, Compilation compilation)
    {
        var builder = ImmutableArray.CreateBuilder<ScreenSignalHandlerModel>();
        var seenAliases = new HashSet<string>(StringComparer.Ordinal);

        for (var current = owner; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is not IMethodSymbol method
                    || method.IsStatic
                    || method.MethodKind != MethodKind.Ordinary)
                {
                    continue;
                }

                var hasOn = false;
                foreach (var attribute in method.GetAttributes())
                {
                    var fullName = Rules.FullName(attribute.AttributeClass);
                    if (fullName == "DigitalBrain.OnAttribute" || fullName == "DigitalBrain.Neurons.OnAttribute")
                    {
                        hasOn = true;
                        break;
                    }
                }

                if (!hasOn)
                {
                    continue;
                }

                if (method.Parameters.Length == 0)
                {
                    continue;
                }

                var signalType = method.Parameters[0].Type;
                if (!DerivesFromSignal(signalType) || signalType.IsAbstract)
                {
                    continue;
                }

                if (method.Parameters.Length > 2
                    || (method.Parameters.Length == 2 && Rules.FullName(method.Parameters[1].Type as INamedTypeSymbol) != "System.Threading.CancellationToken")
                    || method.IsGenericMethod
                    || !compilation.IsSymbolAccessibleWithin(method, compilation.Assembly)
                    || !IsAccessibleForTypeOf(signalType))
                {
                    return ImmutableArray<ScreenSignalHandlerModel>.Empty;
                }

                var returnKind = ClassifyHandlerReturnKind(method);
                if (returnKind == HandlerReturnKind.Unsupported)
                {
                    return ImmutableArray<ScreenSignalHandlerModel>.Empty;
                }

                var alias = SignalAliasOf(signalType) ?? signalType.Name;
                if (!seenAliases.Add(alias))
                {
                    continue;
                }

                builder.Add(new ScreenSignalHandlerModel(
                    alias,
                    signalType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    method.Name,
                    method.Parameters.Length == 2,
                    returnKind));
            }
        }

        return builder.ToImmutable();
    }

    private static HandlerReturnKind ClassifyHandlerReturnKind(IMethodSymbol method)
    {
        if (method.ReturnsVoid)
        {
            return HandlerReturnKind.Void;
        }

        if (method.ReturnType is INamedTypeSymbol named
            && named.ContainingNamespace is { IsGlobalNamespace: false } ns
            && ns.ToDisplayString() == "System.Threading.Tasks")
        {
            if (named.Name == "Task")
            {
                return HandlerReturnKind.Task;
            }
            if (named.Name == "ValueTask")
            {
                return HandlerReturnKind.ValueTask;
            }
        }

        return HandlerReturnKind.Unsupported;
    }

    private static ImmutableArray<SlotPropertyModel> ExtractInheritedSlots(INamedTypeSymbol owner)
    {
        var builder = ImmutableArray.CreateBuilder<SlotPropertyModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var baseType = owner.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            foreach (var member in baseType.GetMembers())
            {
                if (member is not IPropertySymbol property || property.IsStatic)
                {
                    continue;
                }

                AttributeData? slotAttr = null;
                foreach (var attr in property.GetAttributes())
                {
                    if (Rules.FullName(attr.AttributeClass) == "DigitalBrain.SlotAttribute")
                    {
                        slotAttr = attr;
                        break;
                    }
                }

                if (slotAttr is null)
                {
                    continue;
                }

                string? explicitName = null;
                string? key = null;
                var required = true;

                foreach (var named in slotAttr.NamedArguments)
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

                var slotName = explicitName ?? ToCamelCase(property.Name);
                if (!seen.Add(slotName))
                {
                    continue;
                }

                var isNullable = property.Type.NullableAnnotation == NullableAnnotation.Annotated;
                var rawType = property.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
                var isCollection = TryUnwrapSlotCollection(rawType, out var contractType);
                if (!IsAccessibleForTypeOf(contractType))
                {
                    continue;
                }

                builder.Add(new SlotPropertyModel(
                    string.Empty,
                    ImmutableArray<TypeDeclModel>.Empty,
                    owner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    string.Empty,
                    null,
                    ImmutableArray<OperationModel>.Empty,
                    property.Name,
                    AccessibilityKeyword(property.DeclaredAccessibility),
                    contractType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    slotName,
                    key,
                    required,
                    isNullable,
                    isPartial: false,
                    isAccessible: true,
                    isCollection: isCollection));
            }
        }

        return builder.ToImmutable();
    }

    private static SynapseEntryModel? ExtractSynapse(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.TargetSymbol is not IMethodSymbol method
            || method.ContainingType is not { } owner
            || !IsAccessibleOwner(owner))
        {
            return null;
        }

        string? stateAlias = null;
        string? reactSignal = null;

        foreach (var attribute in context.Attributes)
        {
            var args = attribute.ConstructorArguments;
            if (args.Length >= 2)
            {
                if (args[0].Kind == TypedConstantKind.Type
                    && args[0].Value is ITypeSymbol signalType
                    && IsAccessibleForTypeOf(signalType))
                {
                    reactSignal = signalType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                }
                if (args[1].Value is string into && !string.IsNullOrWhiteSpace(into))
                {
                    stateAlias = ExtractStateAlias(into);
                }
            }
            else if (args.Length == 1 && args[0].Value is string from && !string.IsNullOrWhiteSpace(from))
            {
                stateAlias = ExtractStateAlias(from);
            }
            break;
        }

        return new SynapseEntryModel(
            owner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            owner.Name,
            owner.IsStatic,
            stateAlias,
            reactSignal);
    }

    private static string? ExtractStateAlias(string raw)
    {
        var slash = raw.IndexOf('/');
        var state = slash < 0 ? raw : raw.Substring(0, slash);
        return string.IsNullOrWhiteSpace(state) ? null : state.Trim();
    }

    private static ImmutableArray<string> ExtractProvides(INamedTypeSymbol owner)
    {
        var builder = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in owner.GetAttributes())
        {
            if (attribute.AttributeClass?.Name != "ProvidesAttribute")
            {
                continue;
            }

            foreach (var arg in attribute.ConstructorArguments)
            {
                if (arg.Kind == TypedConstantKind.Type && arg.Value is ITypeSymbol singleType && IsAccessibleForTypeOf(singleType))
                {
                    var fullName = singleType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    if (seen.Add(fullName))
                    {
                        builder.Add(fullName);
                    }
                }
                else if (arg.Kind == TypedConstantKind.Array)
                {
                    foreach (var elem in arg.Values)
                    {
                        if (elem.Kind == TypedConstantKind.Type && elem.Value is ITypeSymbol elemType && IsAccessibleForTypeOf(elemType))
                        {
                            var fullName = elemType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                            if (seen.Add(fullName))
                            {
                                builder.Add(fullName);
                            }
                        }
                    }
                }
            }
        }
        return builder.ToImmutable();
    }

    private static ImmutableArray<string> ExtractReacts(INamedTypeSymbol owner)
    {
        var builder = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = owner; current is not null; current = current.BaseType)
        {
            foreach (var attribute in current.GetAttributes())
            {
                var fullName = Rules.FullName(attribute.AttributeClass);
                if (fullName != "DigitalBrain.Neurons.ReactsAttribute" && fullName != "DigitalBrain.ReactsAttribute")
                {
                    continue;
                }

                if (attribute.ConstructorArguments.Length >= 2
                    && attribute.ConstructorArguments[1].Kind == TypedConstantKind.Type
                    && attribute.ConstructorArguments[1].Value is ITypeSymbol signalType
                    && IsAccessibleForTypeOf(signalType))
                {
                    var signalFullName = signalType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    if (seen.Add(signalFullName))
                    {
                        builder.Add(signalFullName);
                    }
                }
            }
        }
        return builder.ToImmutable();
    }

    private static bool IsAccessibleOwner(INamedTypeSymbol owner)
    {
        if (owner.TypeKind != TypeKind.Class || (owner.IsAbstract && !owner.IsStatic))
        {
            return false;
        }

        for (var current = owner; current is not null; current = current.ContainingType)
        {
            if (current.IsGenericType || current.IsFileLocal)
            {
                return false;
            }
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAccessibleForTypeOf(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol || type is IErrorTypeSymbol)
        {
            return false;
        }
        if (type is IArrayTypeSymbol array)
        {
            return IsAccessibleForTypeOf(array.ElementType);
        }
        if (type is not INamedTypeSymbol named || named.IsUnboundGenericType)
        {
            return false;
        }

        for (var current = named; current is not null; current = current.ContainingType)
        {
            if (current.IsFileLocal)
            {
                return false;
            }
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                return false;
            }
            if (current.IsGenericType)
            {
                foreach (var arg in current.TypeArguments)
                {
                    if (!IsAccessibleForTypeOf(arg))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
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

    private static string? ScreenAliasOf(INamedTypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass?.Name == "ScreenAttribute"
                && attribute.ConstructorArguments.Length > 0
                && attribute.ConstructorArguments[0].Value is string alias
                && !string.IsNullOrWhiteSpace(alias))
            {
                return alias.Trim();
            }
        }
        return null;
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
            var inputFullName = IsAccessibleForTypeOf(inputParam)
                ? inputParam.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                : null;
            ops.Add(new OperationModel(
                opName,
                description,
                inputFullName,
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
        ImmutableArray<ComponentEntryModel> modules1,
        ImmutableArray<ComponentEntryModel> modules2,
        ImmutableArray<ComponentEntryModel> screens,
        ImmutableArray<SynapseEntryModel> synapses,
        bool canRegister,
        bool canRegisterScreenSignals,
        string assemblyName)
    {
        if (!properties.IsDefaultOrEmpty)
        {
            var slotGroups = properties
                .GroupBy(static p => p.OwnerFullName, StringComparer.Ordinal)
                .OrderBy(static g => g.Key, StringComparer.Ordinal);

            foreach (var group in slotGroups)
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
                    if (prop.IsCollection)
                    {
                        sb.Append(indent)
                            .Append(prop.Accessibility)
                            .Append(" partial global::DigitalBrain.Components.SlotCollection<")
                            .Append(prop.ContractFullName)
                            .Append("> ")
                            .Append(prop.PropertyName)
                            .Append(" => Slots.GetCollection<")
                            .Append(prop.ContractFullName)
                            .Append(">(")
                            .Append(CSharpLiteral(prop.SlotName))
                            .AppendLine(");");
                    }
                    else
                    {
                        sb.Append(indent)
                            .Append(prop.Accessibility)
                            .Append(" partial ")
                            .Append(prop.ContractFullName)
                            .Append(prop.IsNullable ? "? " : " ")
                            .Append(prop.PropertyName)
                            .Append(prop.IsNullable ? " => Slots.TryGet<" : " => Slots.Get<")
                            .Append(prop.ContractFullName)
                            .Append(">(")
                            .Append(CSharpLiteral(prop.SlotName))
                            .AppendLine(");");
                    }
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
        }

        if (!canRegister)
        {
            return;
        }

        var merged = new Dictionary<string, MergedComponent>(StringComparer.Ordinal);

        if (!properties.IsDefaultOrEmpty)
        {
            foreach (var prop in properties)
            {
                if (!prop.IsAccessible)
                {
                    continue;
                }

                if (!merged.TryGetValue(prop.OwnerFullName, out var entry))
                {
                    entry = new MergedComponent(prop.OwnerFullName, prop.ComponentId, "App", isStatic: false);
                    merged[prop.OwnerFullName] = entry;
                }
                entry.AddSlot(prop);
                entry.Face ??= prop.Face;
                entry.AddOperations(prop.Operations);
            }
        }

        foreach (var mod in modules1.Concat(modules2))
        {
            if (!merged.TryGetValue(mod.OwnerFullName, out var entry))
            {
                entry = new MergedComponent(mod.OwnerFullName, mod.ComponentId, "Module", mod.IsStatic);
                merged[mod.OwnerFullName] = entry;
            }
            else
            {
                entry.ComponentId = mod.ComponentId;
                entry.Kind = "Module";
            }
            entry.AddProvides(mod.Provides);
            entry.AddReacts(mod.Reacts);
        }

        foreach (var screen in screens)
        {
            if (!merged.TryGetValue(screen.OwnerFullName, out var entry))
            {
                entry = new MergedComponent(screen.OwnerFullName, screen.ComponentId, "App", screen.IsStatic);
                merged[screen.OwnerFullName] = entry;
            }
            entry.Face ??= screen.Face;
            entry.AddOperations(screen.Operations);
            foreach (var slot in screen.InheritedSlots)
            {
                entry.AddSlot(slot);
            }
            entry.AddReacts(screen.Reacts);
            entry.AddSignalHandlers(screen.SignalHandlers);
        }

        if (!synapses.IsDefaultOrEmpty)
        {
            foreach (var group in synapses.GroupBy(static s => s.OwnerFullName, StringComparer.Ordinal))
            {
                var items = group.ToList();
                var first = items[0];
                var alias = items.Select(static i => i.StateAlias).FirstOrDefault(static a => !string.IsNullOrWhiteSpace(a))
                    ?? ToCamelCase(first.OwnerName);
                var unitId = alias.StartsWith("unit.", StringComparison.Ordinal) ? alias : "unit." + alias;

                if (!merged.TryGetValue(first.OwnerFullName, out var entry))
                {
                    entry = new MergedComponent(first.OwnerFullName, unitId, "Unit", first.IsStatic);
                    merged[first.OwnerFullName] = entry;
                }
                else if (entry.Kind == "App")
                {
                    entry.Kind = "Unit";
                }

                foreach (var item in items)
                {
                    if (!string.IsNullOrWhiteSpace(item.ReactSignalFullName))
                    {
                        entry.AddReact(item.ReactSignalFullName!);
                    }
                }
            }
        }

        if (merged.Count == 0)
        {
            return;
        }

        var ordered = merged.Values.OrderBy(static c => c.OwnerFullName, StringComparer.Ordinal).ToList();

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

        foreach (var comp in ordered)
        {
            if (!comp.IsStatic)
            {
                initSb.Append("        global::DigitalBrain.Components.ComponentRegistry.Register(typeof(")
                    .Append(comp.OwnerFullName)
                    .Append("), new global::DigitalBrain.Components.ComponentId(")
                    .Append(CSharpLiteral(comp.ComponentId))
                    .AppendLine("));");

                initSb.AppendLine("        global::DigitalBrain.Components.ComponentManifests.Register(");
                initSb.Append("            typeof(").Append(comp.OwnerFullName).AppendLine("),");
            }
            else
            {
                initSb.AppendLine("        global::DigitalBrain.Components.ComponentManifests.Register(");
            }

            initSb.AppendLine("            new global::DigitalBrain.Components.ComponentManifest(");
            initSb.Append("                new global::DigitalBrain.Components.ComponentId(")
                .Append(CSharpLiteral(comp.ComponentId))
                .AppendLine("),");

            var hasExtras = comp.Face is not null
                || comp.Operations.Count > 0
                || !string.Equals(comp.Kind, "App", StringComparison.Ordinal)
                || comp.Provides.Count > 0
                || comp.Reacts.Count > 0;

            if (comp.Slots.Count == 0)
            {
                initSb.Append("                global::System.Array.Empty<global::DigitalBrain.Components.SlotNeed>()")
                    .AppendLine(hasExtras ? "," : "));");
            }
            else
            {
                initSb.AppendLine("                new global::DigitalBrain.Components.SlotNeed[]");
                initSb.AppendLine("                {");
                foreach (var prop in comp.Slots)
                {
                    initSb.Append("                    new global::DigitalBrain.Components.SlotNeed(")
                        .Append(CSharpLiteral(prop.SlotName))
                        .Append(", typeof(")
                        .Append(prop.ContractFullName)
                        .Append("), ")
                        .Append(prop.Required ? "true" : "false")
                        .Append(", ")
                        .Append(prop.Key is null ? "null" : CSharpLiteral(prop.Key));
                    if (prop.IsCollection)
                    {
                        initSb.Append(", true");
                    }
                    initSb.AppendLine("),");
                }
                initSb.Append("                }")
                    .AppendLine(hasExtras ? "," : "));");
            }

            if (hasExtras)
            {
                var extraParts = new List<string>();

                if (comp.Face is { } face)
                {
                    extraParts.Add(
                        "                Face: new global::DigitalBrain.Components.ComponentFace("
                        + CSharpLiteral(face.Title) + ", "
                        + CSharpLiteral(face.Description) + ", "
                        + (face.Icon is null ? "null" : CSharpLiteral(face.Icon)) + ", "
                        + (face.Category is null ? "null" : CSharpLiteral(face.Category)) + ")");
                }

                if (comp.Operations.Count > 0)
                {
                    var opSb = new StringBuilder();
                    opSb.AppendLine("                Operations: new global::DigitalBrain.Components.OperationSpec[]");
                    opSb.AppendLine("                {");
                    foreach (var op in comp.Operations)
                    {
                        opSb.Append("                    new global::DigitalBrain.Components.OperationSpec(")
                            .Append(CSharpLiteral(op.Name))
                            .Append(", ")
                            .Append(CSharpLiteral(op.Description))
                            .Append(", ")
                            .Append(op.InputFullName is null ? "null" : "typeof(" + op.InputFullName + ")")
                            .Append(", ")
                            .Append(CSharpLiteral(op.ParametersJson))
                            .Append(", ")
                            .Append(op.SignalAlias is null ? "null" : CSharpLiteral(op.SignalAlias))
                            .AppendLine("),");
                    }
                    opSb.Append("                }");
                    extraParts.Add(opSb.ToString());
                }

                if (!string.Equals(comp.Kind, "App", StringComparison.Ordinal))
                {
                    extraParts.Add("                Kind: global::DigitalBrain.Components.ComponentKind." + comp.Kind);
                }

                if (comp.Provides.Count > 0)
                {
                    var typesList = string.Join(", ", comp.Provides.Select(static t => "typeof(" + t + ")"));
                    extraParts.Add("                Provides: new global::System.Type[] { " + typesList + " }");
                }

                if (comp.Reacts.Count > 0)
                {
                    var typesList = string.Join(", ", comp.Reacts.Select(static t => "typeof(" + t + ")"));
                    extraParts.Add("                Reacts: new global::System.Type[] { " + typesList + " }");
                }

                for (var i = 0; i < extraParts.Count; i++)
                {
                    initSb.Append(extraParts[i]).AppendLine(i + 1 < extraParts.Count ? "," : "));");
                }
            }

            if (canRegisterScreenSignals && !comp.IsStatic && comp.SignalHandlers.Count > 0)
            {
                initSb.AppendLine("        global::DigitalBrain.Kernel.ScreenSignals.Register(");
                initSb.Append("            typeof(").Append(comp.OwnerFullName).AppendLine("),");
                initSb.AppendLine("            new global::System.Collections.Generic.Dictionary<string, global::DigitalBrain.Kernel.ScreenSignalHandler>(global::System.StringComparer.Ordinal)");
                initSb.AppendLine("            {");
                foreach (var handler in comp.SignalHandlers)
                {
                    var callArgs = handler.TakesCancellationToken
                        ? "(" + handler.SignalTypeFullName + ")signal, ct"
                        : "(" + handler.SignalTypeFullName + ")signal";
                    var invocation = "((" + comp.OwnerFullName + ")screen)." + handler.MethodName + "(" + callArgs + ")";
                    var lambda = handler.ReturnKind switch
                    {
                        HandlerReturnKind.Void => "static (screen, signal, ct) => { " + invocation + "; return global::System.Threading.Tasks.Task.CompletedTask; }",
                        HandlerReturnKind.ValueTask => "static (screen, signal, ct) => " + invocation + ".AsTask()",
                        _ => "static (screen, signal, ct) => " + invocation,
                    };

                    initSb.Append("                [")
                        .Append(CSharpLiteral(handler.Alias))
                        .Append("] = new global::DigitalBrain.Kernel.ScreenSignalHandler(typeof(")
                        .Append(handler.SignalTypeFullName)
                        .Append("), ")
                        .Append(lambda)
                        .AppendLine("),");
                }
                initSb.AppendLine("            });");
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

    private sealed class MergedComponent
    {
        private readonly HashSet<string> _slotNames = new(StringComparer.Ordinal);
        private readonly HashSet<string> _opNames = new(StringComparer.Ordinal);
        private readonly HashSet<string> _providesSet = new(StringComparer.Ordinal);
        private readonly HashSet<string> _reactsSet = new(StringComparer.Ordinal);
        private readonly HashSet<string> _handlerAliases = new(StringComparer.Ordinal);

        public MergedComponent(string ownerFullName, string componentId, string kind, bool isStatic)
        {
            OwnerFullName = ownerFullName;
            ComponentId = componentId;
            Kind = kind;
            IsStatic = isStatic;
        }

        public string OwnerFullName { get; }
        public string ComponentId { get; set; }
        public string Kind { get; set; }
        public bool IsStatic { get; }
        public ComponentFaceModel? Face { get; set; }
        public List<SlotPropertyModel> Slots { get; } = new();
        public List<OperationModel> Operations { get; } = new();
        public List<string> Provides { get; } = new();
        public List<string> Reacts { get; } = new();
        public List<ScreenSignalHandlerModel> SignalHandlers { get; } = new();

        public void AddSlot(SlotPropertyModel slot)
        {
            if (_slotNames.Add(slot.SlotName))
            {
                Slots.Add(slot);
            }
        }

        public void AddOperations(ImmutableArray<OperationModel> ops)
        {
            foreach (var op in ops)
            {
                if (_opNames.Add(op.Name))
                {
                    Operations.Add(op);
                }
            }
        }

        public void AddProvides(ImmutableArray<string> provides)
        {
            foreach (var type in provides)
            {
                if (_providesSet.Add(type))
                {
                    Provides.Add(type);
                }
            }
        }

        public void AddReact(string signalType)
        {
            if (_reactsSet.Add(signalType))
            {
                Reacts.Add(signalType);
            }
        }

        public void AddReacts(ImmutableArray<string> signalTypes)
        {
            foreach (var signalType in signalTypes)
            {
                AddReact(signalType);
            }
        }

        public void AddSignalHandlers(ImmutableArray<ScreenSignalHandlerModel> handlers)
        {
            foreach (var handler in handlers)
            {
                if (_handlerAliases.Add(handler.Alias))
                {
                    SignalHandlers.Add(handler);
                }
            }
        }
    }

    private enum HandlerReturnKind
    {
        Unsupported = 0,
        Void = 1,
        Task = 2,
        ValueTask = 3,
    }

    private sealed class ScreenSignalHandlerModel : IEquatable<ScreenSignalHandlerModel>
    {
        public ScreenSignalHandlerModel(
            string alias,
            string signalTypeFullName,
            string methodName,
            bool takesCancellationToken,
            HandlerReturnKind returnKind)
        {
            Alias = alias;
            SignalTypeFullName = signalTypeFullName;
            MethodName = methodName;
            TakesCancellationToken = takesCancellationToken;
            ReturnKind = returnKind;
        }

        public string Alias { get; }
        public string SignalTypeFullName { get; }
        public string MethodName { get; }
        public bool TakesCancellationToken { get; }
        public HandlerReturnKind ReturnKind { get; }

        public bool Equals(ScreenSignalHandlerModel? other)
            => other is not null
               && string.Equals(Alias, other.Alias, StringComparison.Ordinal)
               && string.Equals(SignalTypeFullName, other.SignalTypeFullName, StringComparison.Ordinal)
               && string.Equals(MethodName, other.MethodName, StringComparison.Ordinal)
               && TakesCancellationToken == other.TakesCancellationToken
               && ReturnKind == other.ReturnKind;

        public override bool Equals(object? obj) => Equals(obj as ScreenSignalHandlerModel);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(Alias);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(SignalTypeFullName);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(MethodName);
                hash = (hash * 397) ^ (TakesCancellationToken ? 1 : 0);
                hash = (hash * 397) ^ (int)ReturnKind;
                return hash;
            }
        }
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
        public OperationModel(string name, string description, string? inputFullName, string parametersJson, string? signalAlias)
        {
            Name = name;
            Description = description;
            InputFullName = inputFullName;
            ParametersJson = parametersJson;
            SignalAlias = signalAlias;
        }

        public string Name { get; }
        public string Description { get; }
        public string? InputFullName { get; }
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
                hash = (hash * 397) ^ (InputFullName is null ? 0 : StringComparer.Ordinal.GetHashCode(InputFullName));
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(ParametersJson);
                hash = (hash * 397) ^ (SignalAlias is null ? 0 : StringComparer.Ordinal.GetHashCode(SignalAlias));
                return hash;
            }
        }
    }

    private sealed class ComponentEntryModel : IEquatable<ComponentEntryModel>
    {
        public ComponentEntryModel(
            string ownerFullName,
            string componentId,
            string kind,
            bool isStatic,
            ComponentFaceModel? face,
            ImmutableArray<OperationModel> operations,
            ImmutableArray<string> provides,
            ImmutableArray<SlotPropertyModel> inheritedSlots,
            ImmutableArray<string> reacts,
            ImmutableArray<ScreenSignalHandlerModel> signalHandlers)
        {
            OwnerFullName = ownerFullName;
            ComponentId = componentId;
            Kind = kind;
            IsStatic = isStatic;
            Face = face;
            Operations = operations;
            Provides = provides;
            InheritedSlots = inheritedSlots;
            Reacts = reacts;
            SignalHandlers = signalHandlers;
        }

        public string OwnerFullName { get; }
        public string ComponentId { get; }
        public string Kind { get; }
        public bool IsStatic { get; }
        public ComponentFaceModel? Face { get; }
        public ImmutableArray<OperationModel> Operations { get; }
        public ImmutableArray<string> Provides { get; }
        public ImmutableArray<SlotPropertyModel> InheritedSlots { get; }
        public ImmutableArray<string> Reacts { get; }
        public ImmutableArray<ScreenSignalHandlerModel> SignalHandlers { get; }

        public bool Equals(ComponentEntryModel? other)
        {
            if (other is null
                || !string.Equals(OwnerFullName, other.OwnerFullName, StringComparison.Ordinal)
                || !string.Equals(ComponentId, other.ComponentId, StringComparison.Ordinal)
                || !string.Equals(Kind, other.Kind, StringComparison.Ordinal)
                || IsStatic != other.IsStatic
                || !Equals(Face, other.Face)
                || Operations.Length != other.Operations.Length
                || Provides.Length != other.Provides.Length
                || InheritedSlots.Length != other.InheritedSlots.Length
                || Reacts.Length != other.Reacts.Length
                || SignalHandlers.Length != other.SignalHandlers.Length)
            {
                return false;
            }
            for (var i = 0; i < Operations.Length; i++)
            {
                if (!Operations[i].Equals(other.Operations[i])) { return false; }
            }
            for (var i = 0; i < Provides.Length; i++)
            {
                if (!string.Equals(Provides[i], other.Provides[i], StringComparison.Ordinal)) { return false; }
            }
            for (var i = 0; i < InheritedSlots.Length; i++)
            {
                if (!InheritedSlots[i].Equals(other.InheritedSlots[i])) { return false; }
            }
            for (var i = 0; i < Reacts.Length; i++)
            {
                if (!string.Equals(Reacts[i], other.Reacts[i], StringComparison.Ordinal)) { return false; }
            }
            for (var i = 0; i < SignalHandlers.Length; i++)
            {
                if (!SignalHandlers[i].Equals(other.SignalHandlers[i])) { return false; }
            }
            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as ComponentEntryModel);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(OwnerFullName);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(ComponentId);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(Kind);
                hash = (hash * 397) ^ (Face?.GetHashCode() ?? 0);
                hash = (hash * 397) ^ Operations.Length;
                hash = (hash * 397) ^ Provides.Length;
                hash = (hash * 397) ^ InheritedSlots.Length;
                hash = (hash * 397) ^ Reacts.Length;
                hash = (hash * 397) ^ SignalHandlers.Length;
                return hash;
            }
        }
    }

    private sealed class SynapseEntryModel : IEquatable<SynapseEntryModel>
    {
        public SynapseEntryModel(
            string ownerFullName,
            string ownerName,
            bool isStatic,
            string? stateAlias,
            string? reactSignalFullName)
        {
            OwnerFullName = ownerFullName;
            OwnerName = ownerName;
            IsStatic = isStatic;
            StateAlias = stateAlias;
            ReactSignalFullName = reactSignalFullName;
        }

        public string OwnerFullName { get; }
        public string OwnerName { get; }
        public bool IsStatic { get; }
        public string? StateAlias { get; }
        public string? ReactSignalFullName { get; }

        public bool Equals(SynapseEntryModel? other)
            => other is not null
               && string.Equals(OwnerFullName, other.OwnerFullName, StringComparison.Ordinal)
               && string.Equals(OwnerName, other.OwnerName, StringComparison.Ordinal)
               && IsStatic == other.IsStatic
               && string.Equals(StateAlias, other.StateAlias, StringComparison.Ordinal)
               && string.Equals(ReactSignalFullName, other.ReactSignalFullName, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as SynapseEntryModel);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(OwnerFullName);
                hash = (hash * 397) ^ (StateAlias is null ? 0 : StringComparer.Ordinal.GetHashCode(StateAlias));
                hash = (hash * 397) ^ (ReactSignalFullName is null ? 0 : StringComparer.Ordinal.GetHashCode(ReactSignalFullName));
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
            bool isNullable,
            bool isPartial,
            bool isAccessible,
            bool isCollection)
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
            IsNullable = isNullable;
            IsPartial = isPartial;
            IsAccessible = isAccessible;
            IsCollection = isCollection;
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
        public bool IsNullable { get; }
        public bool IsPartial { get; }
        public bool IsAccessible { get; }
        public bool IsCollection { get; }

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
                || IsNullable != other.IsNullable
                || IsPartial != other.IsPartial
                || IsAccessible != other.IsAccessible
                || IsCollection != other.IsCollection
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
                hash = (hash * 397) ^ (IsCollection ? 1 : 0);
                return hash;
            }
        }
    }
}
