using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DigitalBrain.Analyzers.Neurons;

/// <summary>
/// Emits a <c>[ModuleInitializer]</c> that roots <c>[Module]</c> classes, <c>[Neuron]</c> classes, <c>[Signal]</c> records,
/// <c>[State]</c> records, <c>IModuleOptions</c> implementations and <c>[NeuronAlias]</c> contracts for Native AOT trimming,
/// and generates compile-time <c>NeuronProxy</c> subclasses for neuron contracts without altering the public API surface.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class AotPreservationGenerator : IIncrementalGenerator
{
    private static readonly SymbolDisplayFormat FullFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly SymbolDisplayFormat BareTypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var types = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is BaseTypeDeclarationSyntax,
                static (ctx, ct) => Inspect(ctx, ct))
            .Where(static item => item is not null)
            .Select(static (item, _) => item!)
            .Collect();

        var hasProxy = context.CompilationProvider.Select(static (comp, _) =>
        {
            var assemblyName = comp.AssemblyName ?? "";
            var decl = comp.GetTypeByMetadataName("DigitalBrain.Invocation.NeuronProxy");
            if (decl is null || decl.GetMembers("Initialize").OfType<IMethodSymbol>().FirstOrDefault() is not { } init)
            {
                return (HasProxy: false, AssemblyName: assemblyName);
            }
            return (HasProxy: comp.IsSymbolAccessibleWithin(init, comp.Assembly), AssemblyName: assemblyName);
        });

        context.RegisterSourceOutput(types.Combine(hasProxy), static (spc, pair) => Emit(spc, pair.Left, pair.Right.HasProxy, pair.Right.AssemblyName));
    }

    private sealed class InspectedItem : IEquatable<InspectedItem>
    {
        public InspectedItem(
            string typeDisplay,
            bool isInterface,
            bool isValueType,
            bool isEnum,
            bool registerJsonType,
            ContractProxySpec? proxy,
            ImmutableArray<DiscoveredType> referencedTypes)
        {
            TypeDisplay = typeDisplay;
            IsInterface = isInterface;
            IsValueType = isValueType;
            IsEnum = isEnum;
            RegisterJsonType = registerJsonType;
            Proxy = proxy;
            ReferencedTypes = referencedTypes;
        }

        public string TypeDisplay { get; }
        public bool IsInterface { get; }
        public bool IsValueType { get; }
        public bool IsEnum { get; }
        public bool RegisterJsonType { get; }
        public ContractProxySpec? Proxy { get; }
        public ImmutableArray<DiscoveredType> ReferencedTypes { get; }

        public bool Equals(InspectedItem? other)
            => other is not null
               && string.Equals(TypeDisplay, other.TypeDisplay, StringComparison.Ordinal)
               && IsInterface == other.IsInterface
               && IsValueType == other.IsValueType
               && IsEnum == other.IsEnum
               && RegisterJsonType == other.RegisterJsonType
               && string.Equals(Proxy?.Source, other.Proxy?.Source, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as InspectedItem);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(TypeDisplay);
    }

    private readonly struct DiscoveredType
    {
        public DiscoveredType(string typeDisplay, bool isValueType)
        {
            TypeDisplay = typeDisplay;
            IsValueType = isValueType;
        }

        public string TypeDisplay { get; }
        public bool IsValueType { get; }
    }

    private sealed class ContractProxySpec
    {
        public ContractProxySpec(string className, string contractTypeDisplay, string source)
        {
            ClassName = className;
            ContractTypeDisplay = contractTypeDisplay;
            Source = source;
        }

        public string ClassName { get; }
        public string ContractTypeDisplay { get; }
        public string Source { get; }
    }

    private static InspectedItem? Inspect(GeneratorSyntaxContext context, System.Threading.CancellationToken cancellationToken)
    {
        var syntax = (BaseTypeDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(syntax, cancellationToken) is not INamedTypeSymbol symbol)
        {
            return null;
        }

        if (symbol.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface or TypeKind.Enum))
        {
            return null;
        }

        if (symbol.IsAbstract && symbol.TypeKind != TypeKind.Interface)
        {
            return null;
        }

        if (symbol.IsGenericType)
        {
            return null;
        }

        if (symbol.IsRefLikeType || symbol.IsStatic)
        {
            return null;
        }

        if (!SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, context.SemanticModel.Compilation.Assembly))
        {
            return null;
        }

        if (!IsAccessibleInCompilation(symbol))
        {
            return null;
        }

        var isEnum = symbol.TypeKind == TypeKind.Enum;
        var isContractInterface = symbol.TypeKind == TypeKind.Interface && IsNeuronContract(symbol);
        var isDataRecordOrDto = !isContractInterface
            && symbol.TypeKind is TypeKind.Class or TypeKind.Struct
            && (symbol.IsRecord
                || Rules.DerivesFrom(symbol, Rules.SignalType)
                || Rules.Has(symbol, Rules.StateAttribute)
                || Rules.Has(symbol, "DigitalBrain.State.ExportableStateAttribute")
                || Rules.Has(symbol, "Orleans.GenerateSerializerAttribute"));
        var registerJsonType = isEnum || isDataRecordOrDto;
        var keep = isContractInterface
            || registerJsonType
            || Rules.DerivesFrom(symbol, Rules.ModuleType)
            || Rules.DerivesFrom(symbol, Rules.NeuronType)
            || Rules.Has(symbol, "Orleans.RegisterConverterAttribute")
            || Rules.Has(symbol, "Orleans.RegisterCopierAttribute")
            || Rules.Has(symbol, "Orleans.RegisterSerializerAttribute")
            || Rules.Has(symbol, "Orleans.RegisterActivatorAttribute")
            || ImplementsOptionsOrPort(symbol);

        if (!keep)
        {
            return null;
        }

        var display = symbol.ToDisplayString(BareTypeFormat);
        ContractProxySpec? proxy = isContractInterface ? TryBuildProxySpec(symbol, display, context.SemanticModel.Compilation.Assembly) : null;
        var referenced = ImmutableArray.CreateBuilder<DiscoveredType>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        if (isContractInterface)
        {
            foreach (var iface in symbol.AllInterfaces.Add(symbol))
            {
                foreach (var m in iface.GetMembers().OfType<IMethodSymbol>())
                {
                    CollectJsonTypes(m.ReturnType, context.SemanticModel.Compilation, referenced, visited, 0);
                    foreach (var p in m.Parameters)
                    {
                        CollectJsonTypes(p.Type, context.SemanticModel.Compilation, referenced, visited, 0);
                    }
                }
            }
        }
        else if (isDataRecordOrDto)
        {
            CollectJsonTypes(symbol, context.SemanticModel.Compilation, referenced, visited, 0);
        }

        return new InspectedItem(
            display,
            symbol.TypeKind == TypeKind.Interface,
            symbol.IsValueType,
            isEnum,
            registerJsonType && !ImplementsServiceOrGrain(symbol),
            proxy,
            referenced.ToImmutable());
    }

    private static bool IsAccessibleInCompilation(INamedTypeSymbol symbol)
    {
        for (var cur = symbol; cur is not null; cur = cur.ContainingType)
        {
            if (cur.DeclaredAccessibility != Accessibility.Public && cur.DeclaredAccessibility != Accessibility.Internal)
            {
                return false;
            }
            if (cur.IsGenericType)
            {
                return false;
            }
        }
        return true;
    }

    private static bool ImplementsServiceOrGrain(INamedTypeSymbol symbol)
    {
        if (Rules.DerivesFrom(symbol, Rules.ModuleType) || Rules.DerivesFrom(symbol, Rules.NeuronType) || Rules.DerivesFrom(symbol, "Orleans.Grain"))
        {
            return true;
        }
        foreach (var iface in symbol.AllInterfaces)
        {
            if (iface.Name.StartsWith("IGrainWith", StringComparison.Ordinal) || iface.Name is "ISystemTarget" or "IStartupTask" or "ILifecycleParticipant")
            {
                return true;
            }
        }
        return false;
    }

    private static void CollectJsonTypes(
        ITypeSymbol? type,
        Compilation compilation,
        ImmutableArray<DiscoveredType>.Builder output,
        HashSet<string> visited,
        int depth)
    {
        if (type is null || depth > 5)
        {
            return;
        }

        if (type is IArrayTypeSymbol arr)
        {
            CollectJsonTypes(arr.ElementType, compilation, output, visited, depth + 1);
            return;
        }

        if (type is not INamedTypeSymbol named)
        {
            return;
        }

        if (named.IsGenericType)
        {
            foreach (var arg in named.TypeArguments)
            {
                CollectJsonTypes(arg, compilation, output, visited, depth + 1);
            }
            return;
        }

        if (named.SpecialType != SpecialType.None
            || named.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Enum)
            || named.IsAbstract
            || named.IsStatic
            || named.IsRefLikeType)
        {
            return;
        }

        var full = Rules.FullName(named);
        if (full.StartsWith("System.", StringComparison.Ordinal)
            || full.StartsWith("Microsoft.", StringComparison.Ordinal)
            || full.StartsWith("Orleans.", StringComparison.Ordinal))
        {
            return;
        }

        if (!SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, compilation.Assembly))
        {
            return;
        }

        if (!compilation.IsSymbolAccessibleWithin(named, compilation.Assembly))
        {
            return;
        }

        for (var outer = named.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            if (outer.IsGenericType || !compilation.IsSymbolAccessibleWithin(outer, compilation.Assembly))
            {
                return;
            }
        }

        if (ImplementsServiceOrGrain(named))
        {
            return;
        }

        var display = named.ToDisplayString(BareTypeFormat);
        if (!visited.Add(display))
        {
            return;
        }

        output.Add(new DiscoveredType(display, named.IsValueType));

        if (named.TypeKind != TypeKind.Enum)
        {
            foreach (var prop in named.GetMembers().OfType<IPropertySymbol>())
            {
                if (!prop.IsStatic && prop.DeclaredAccessibility == Accessibility.Public)
                {
                    CollectJsonTypes(prop.Type, compilation, output, visited, depth + 1);
                }
            }
        }
    }

    private static bool IsNeuronContract(INamedTypeSymbol symbol)
    {
        if (Rules.Has(symbol, "DigitalBrain.Contracts.NeuronAliasAttribute")
            || Rules.Has(symbol, "DigitalBrain.Contracts.DefaultNeuronTypeAttribute")
            || Rules.Has(symbol, "DigitalBrain.Contracts.ConnectionContractAttribute")
            || Rules.Has(symbol, Rules.CapabilityAttribute))
        {
            return true;
        }

        var full = Rules.FullName(symbol);
        return full is "DigitalBrain.Flows.IFlowCallbacks"
            || symbol.Name is "INotifications" or "IFolder" or "ITableHandle" or "ILocalDisk";
    }

    private static ContractProxySpec? TryBuildProxySpec(INamedTypeSymbol iface, string contractTypeDisplay, IAssemblySymbol currentAssembly)
    {
        var allInterfaces = iface.AllInterfaces.Add(iface);
        var methods = new List<IMethodSymbol>();
        var seenSignatures = new HashSet<string>(StringComparer.Ordinal);

        foreach (var current in allInterfaces)
        {
            var currentFull = Rules.FullName(current);
            if (currentFull is "System.IDisposable" or "System.IAsyncDisposable")
            {
                return null;
            }

            if (!SymbolEqualityComparer.Default.Equals(current.ContainingAssembly, currentAssembly))
            {
                var currentAsmName = current.ContainingAssembly.Name;
                if (!currentAsmName.StartsWith("System", StringComparison.Ordinal)
                    && !currentAsmName.StartsWith("DigitalBrain", StringComparison.Ordinal)
                    && !currentAsmName.EndsWith(".Contracts", StringComparison.Ordinal))
                {
                    return null;
                }
            }

            foreach (var member in current.GetMembers())
            {
                if (member.IsStatic)
                {
                    continue;
                }

                if (member is IPropertySymbol or IEventSymbol)
                {
                    return null;
                }

                if (member is IMethodSymbol method)
                {
                    if (method.MethodKind != MethodKind.Ordinary || method.IsGenericMethod)
                    {
                        return null;
                    }

                    if (method.Parameters.Any(static p => p.RefKind != RefKind.None))
                    {
                        return null;
                    }

                    if (!IsSupportedReturnType(method.ReturnType))
                    {
                        return null;
                    }

                    var sigKey = method.Name + "(" + string.Join(",", method.Parameters.Select(static p => p.Type.ToDisplayString(BareTypeFormat))) + ")";
                    if (seenSignatures.Add(sigKey))
                    {
                        methods.Add(method);
                    }
                }
            }
        }

        var safeAsm = SanitizeIdentifier(currentAssembly.Name);
        var safeName = SanitizeIdentifier(contractTypeDisplay);
        var className = "AotNeuronProxy_" + safeAsm + "_" + safeName;

        var sb = new StringBuilder();
        sb.Append("internal sealed class ").Append(className)
            .Append(" : global::DigitalBrain.Invocation.NeuronProxy, ")
            .AppendLine(contractTypeDisplay);
        sb.AppendLine("{");

        for (var mIdx = 0; mIdx < methods.Count; mIdx++)
        {
            var method = methods[mIdx];
            var declaringDisplay = method.ContainingType.ToDisplayString(BareTypeFormat);
            var returnBare = method.ReturnType.ToDisplayString(BareTypeFormat);
            var returnFull = method.ReturnType.ToDisplayString(FullFormat);
            var paramTypesArray = method.Parameters.Length == 0
                ? "global::System.Type.EmptyTypes"
                : "new global::System.Type[] { " + string.Join(", ", method.Parameters.Select(static p => "typeof(" + p.Type.ToDisplayString(BareTypeFormat) + ")")) + " }";

            sb.Append("    private static readonly global::System.Reflection.MethodInfo _m").Append(mIdx)
                .Append(" = typeof(").Append(declaringDisplay).Append(").GetMethod(")
                .Append(SymbolDisplay.FormatLiteral(method.Name, quote: true)).Append(", ")
                .Append(paramTypesArray).AppendLine(")!;");

            var paramDecls = string.Join(", ", method.Parameters.Select(static (p, idx) =>
                p.Type.ToDisplayString(FullFormat) + " p" + idx));
            var argValues = method.Parameters.Length == 0
                ? "global::System.Array.Empty<object?>()"
                : "new object?[] { " + string.Join(", ", method.Parameters.Select(static (_, idx) => "(object?)p" + idx)) + " }";

            sb.Append("    ").Append(returnFull).Append(' ').Append(declaringDisplay).Append('.').Append(method.Name)
                .Append('(').Append(paramDecls).AppendLine(")");
            var isOneWay = method.GetAttributes().Any(static a => Rules.FullName(a.AttributeClass) == "DigitalBrain.Contracts.OneWayCallAttribute");
            var retNamed = method.ReturnType as INamedTypeSymbol;
            var retOriginal = retNamed is not null ? Rules.FullName(retNamed.OriginalDefinition) : "";
            if (isOneWay)
            {
                if (method.ReturnsVoid)
                {
                    sb.Append("        => _ = base.CallOneWay(_m").Append(mIdx).Append(", ").Append(argValues).AppendLine(");");
                }
                else if (retOriginal == "System.Threading.Tasks.ValueTask")
                {
                    sb.Append("        => new global::System.Threading.Tasks.ValueTask(base.CallOneWay(_m").Append(mIdx).Append(", ").Append(argValues).AppendLine("));");
                }
                else
                {
                    sb.Append("        => base.CallOneWay(_m").Append(mIdx).Append(", ").Append(argValues).AppendLine(");");
                }
            }
            else if (retOriginal == "System.Threading.Tasks.Task")
            {
                sb.Append("        => base.CallNone(_m").Append(mIdx).Append(", ").Append(argValues).AppendLine(");");
            }
            else if (retOriginal == "System.Threading.Tasks.ValueTask")
            {
                sb.Append("        => new global::System.Threading.Tasks.ValueTask(base.CallNone(_m").Append(mIdx).Append(", ").Append(argValues).AppendLine("));");
            }
            else if (retOriginal == "System.Threading.Tasks.Task`1" && retNamed is { TypeArguments.Length: 1 })
            {
                var argTypeFull = retNamed.TypeArguments[0].ToDisplayString(FullFormat);
                sb.Append("        => base.CallTask<").Append(argTypeFull).Append(">(_m").Append(mIdx).Append(", ").Append(argValues).AppendLine(");");
            }
            else if (retOriginal == "System.Threading.Tasks.ValueTask`1" && retNamed is { TypeArguments.Length: 1 })
            {
                var argTypeFull = retNamed.TypeArguments[0].ToDisplayString(FullFormat);
                sb.Append("        => base.CallValueTask<").Append(argTypeFull).Append(">(_m").Append(mIdx).Append(", ").Append(argValues).AppendLine(");");
            }
            else if (retOriginal == "System.Collections.Generic.IAsyncEnumerable`1" && retNamed is { TypeArguments.Length: 1 })
            {
                var argTypeFull = retNamed.TypeArguments[0].ToDisplayString(FullFormat);
                sb.Append("        => base.CallStream<").Append(argTypeFull).Append(">(_m").Append(mIdx).Append(", ").Append(argValues).AppendLine(");");
            }
            else if (method.ReturnsVoid)
            {
                sb.Append("        => _ = base.Invoke(_m").Append(mIdx).Append(", ").Append(argValues).AppendLine(");");
            }
            else
            {
                sb.Append("        => (").Append(returnFull).Append(")base.Invoke(_m").Append(mIdx).Append(", ").Append(argValues).AppendLine(")!;");
            }
        }

        sb.AppendLine("}");
        return new ContractProxySpec(className, contractTypeDisplay, sb.ToString());
    }

    private static bool IsSupportedReturnType(ITypeSymbol returnType)
    {
        if (returnType.SpecialType == SpecialType.System_Void)
        {
            return true;
        }

        if (returnType is not INamedTypeSymbol named)
        {
            return false;
        }

        var full = Rules.FullName(named.OriginalDefinition);
        return full is "System.Threading.Tasks.Task"
            or "System.Threading.Tasks.Task`1"
            or "System.Threading.Tasks.ValueTask"
            or "System.Threading.Tasks.ValueTask`1"
            or "System.Collections.Generic.IAsyncEnumerable`1";
    }

    private static string SanitizeIdentifier(string display)
    {
        var sb = new StringBuilder(display.Length);
        foreach (var ch in display)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }
        return sb.ToString();
    }

    private static bool ImplementsOptionsOrPort(INamedTypeSymbol symbol)
    {
        if (symbol.Name.EndsWith("Options", StringComparison.Ordinal)
            || symbol.Name.EndsWith("Port", StringComparison.Ordinal)
            || symbol.Name.EndsWith("PriceList", StringComparison.Ordinal)
            || symbol.Name.EndsWith("Surrogate", StringComparison.Ordinal))
        {
            return true;
        }
        foreach (var iface in symbol.AllInterfaces)
        {
            if (iface.Name == "IModuleOptions" || iface.Name.StartsWith("IGrainWith", StringComparison.Ordinal) || iface.Name == "ISystemTarget")
            {
                return true;
            }
        }
        return false;
    }

    private static void Emit(SourceProductionContext context, ImmutableArray<InspectedItem> items, bool hasProxy, string assemblyName)
    {
        if (items.IsDefaultOrEmpty)
        {
            return;
        }

        var distinct = items
            .GroupBy(static x => x.TypeDisplay, StringComparer.Ordinal)
            .Select(static g => g.First())
            .OrderBy(static x => x.TypeDisplay, StringComparer.Ordinal)
            .ToArray();

        var jsonTypes = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in distinct)
        {
            if (item.RegisterJsonType)
            {
                jsonTypes[item.TypeDisplay] = item.IsValueType;
            }
            foreach (var disc in item.ReferencedTypes)
            {
                jsonTypes[disc.TypeDisplay] = disc.IsValueType;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CA2255, IL2026, IL2072, IL2111, CS0436, CS0612, CS0618");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Diagnostics.CodeAnalysis;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine();
        sb.AppendLine("namespace System.Runtime.CompilerServices;");
        sb.AppendLine();

        if (hasProxy)
        {
            foreach (var item in distinct)
            {
                if (item.Proxy is { } proxy)
                {
                    sb.AppendLine("[global::System.Runtime.CompilerServices.CompilerGenerated]");
                    sb.AppendLine(proxy.Source);
                }
            }
        }

        var dynamicDeps = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in distinct)
        {
            dynamicDeps[item.TypeDisplay] = item.IsInterface;
        }
        foreach (var kv in jsonTypes)
        {
            if (!dynamicDeps.ContainsKey(kv.Key))
            {
                dynamicDeps[kv.Key] = false;
            }
        }

        var rootClassName = "AotRoot_" + SanitizeIdentifier(assemblyName);
        sb.AppendLine("[global::System.Runtime.CompilerServices.CompilerGenerated]");
        sb.Append("internal static class ").AppendLine(rootClassName);
        sb.AppendLine("{");
        foreach (var kv in dynamicDeps.OrderBy(static x => x.Key, StringComparer.Ordinal))
        {
            var flags = kv.Value
                ? "DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.Interfaces"
                : "DynamicallyAccessedMemberTypes.PublicParameterlessConstructor | DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields | DynamicallyAccessedMemberTypes.Interfaces";
            sb.Append("    [DynamicDependency(").Append(flags).Append(", typeof(").Append(kv.Key).AppendLine("))]");
        }
        sb.AppendLine("    [ModuleInitializer]");
        sb.AppendLine("    internal static void Initialize()");
        sb.AppendLine("    {");
        if (hasProxy)
        {
            foreach (var item in distinct)
            {
                if (item.Proxy is { } proxy)
                {
                    sb.Append("        global::DigitalBrain.Invocation.NeuronProxy.Register<")
                        .Append(proxy.ContractTypeDisplay)
                        .Append(">(static (id, sink) => global::DigitalBrain.Invocation.NeuronProxy.Initialize<")
                        .Append(proxy.ContractTypeDisplay)
                        .Append(">(new ")
                        .Append(proxy.ClassName)
                        .AppendLine("(), id, sink));");
                }
            }
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");

        context.AddSource("DigitalBrain.AotPreservation.g.cs", sb.ToString());
    }
}
