using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace DigitalBrain.Analyzers.Contracts;

/// <summary>
/// Emits one <c>ToolSchema</c> per method of every <c>[NeuronAlias]</c> interface in the compilation and registers them
/// via an assembly <c>[ModuleInitializer]</c> without runtime reflection.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ToolSchemaGenerator : IIncrementalGenerator
{
    private const int MaxToolNameLength = 64;
    private const int MaxObjectDepth = 4;
    private static readonly char[] AliasSeparators = { '.', '-' };

    private static readonly SymbolDisplayFormat TypeKeyFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var contracts = context.SyntaxProvider.ForAttributeWithMetadataName(
                "DigitalBrain.Contracts.NeuronAliasAttribute",
                predicate: static (node, _) => node is InterfaceDeclarationSyntax,
                transform: static (ctx, ct) => ExtractContract(ctx, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect();

        var hasToolSchemas = context.CompilationProvider.Select(static (comp, _) =>
        {
            var assemblyName = comp.AssemblyName ?? string.Empty;
            var decl = comp.GetTypeByMetadataName("DigitalBrain.Contracts.ToolSchemas");
            if (decl is null || decl.GetMembers("Register").OfType<IMethodSymbol>().FirstOrDefault() is not { } register)
            {
                return (HasToolSchemas: false, AssemblyName: assemblyName);
            }
            return (HasToolSchemas: comp.IsSymbolAccessibleWithin(register, comp.Assembly), AssemblyName: assemblyName);
        });

        context.RegisterSourceOutput(
            contracts.Combine(hasToolSchemas),
            static (spc, pair) => Emit(spc, pair.Left, pair.Right.HasToolSchemas, pair.Right.AssemblyName));
    }

    private static ContractModel? ExtractContract(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not INamedTypeSymbol iface
            || iface.TypeKind != TypeKind.Interface
            || iface.IsGenericType)
        {
            return null;
        }

        string? alias = null;
        foreach (var attribute in context.Attributes)
        {
            if (attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is string raw && !string.IsNullOrWhiteSpace(raw))
            {
                alias = raw;
                break;
            }
        }
        if (alias is null)
        {
            return null;
        }

        var contractFullName = ClrFullName(iface);
        var defaultKey = FindStringConstant(iface, "DefaultKey");
        var keyPattern = FindStringConstant(iface, "KeyPattern");

        var methods = CollectEligibleMethods(iface);
        if (methods.Count == 0)
        {
            return null;
        }

        var groups = new List<(string Name, List<IMethodSymbol> Overloads)>();
        var groupIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var method in methods)
        {
            if (!groupIndex.TryGetValue(method.Name, out var index))
            {
                index = groups.Count;
                groupIndex[method.Name] = index;
                groups.Add((method.Name, new List<IMethodSymbol>()));
            }
            groups[index].Overloads.Add(method);
        }

        var schemas = ImmutableArray.CreateBuilder<SchemaModel>(groups.Count);
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticModel>();

        foreach (var (methodName, overloads) in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var toolName = BuildToolName(alias, methodName);
            if (toolName.Length > MaxToolNameLength)
            {
                var location = overloads[0].Locations.FirstOrDefault() ?? iface.Locations.FirstOrDefault() ?? Location.None;
                var lineSpan = location.GetLineSpan();
                diagnostics.Add(new DiagnosticModel(
                    location.SourceTree?.FilePath ?? string.Empty,
                    location.SourceSpan,
                    lineSpan.Span,
                    toolName,
                    iface.Name,
                    methodName));
            }

            var description = ResolveMethodDescription(overloads, methodName, alias, cancellationToken);
            var readOnly = overloads.All(static m => Rules.Has(m, "DigitalBrain.Contracts.ReadOnlyCallAttribute"));
            var parametersJson = BuildParametersJson(overloads, defaultKey, keyPattern);

            schemas.Add(new SchemaModel(
                contractFullName,
                toolName,
                methodName,
                description,
                parametersJson,
                defaultKey,
                keyPattern,
                readOnly));
        }

        return new ContractModel(contractFullName, schemas.ToImmutable(), diagnostics.ToImmutable());
    }

    private static List<IMethodSymbol> CollectEligibleMethods(INamedTypeSymbol iface)
    {
        var result = new List<IMethodSymbol>();
        var seenSignatures = new HashSet<string>(StringComparer.Ordinal);
        var hierarchy = new List<INamedTypeSymbol> { iface };
        foreach (var inherited in iface.AllInterfaces)
        {
            hierarchy.Add(inherited);
        }

        foreach (var current in hierarchy)
        {
            var full = Rules.FullName(current);
            if (full is "System.IDisposable" or "System.IAsyncDisposable")
            {
                continue;
            }

            foreach (var member in current.GetMembers())
            {
                if (member is not IMethodSymbol method
                    || method.IsStatic
                    || method.MethodKind != MethodKind.Ordinary
                    || method.IsGenericMethod
                    || method.Parameters.Any(static p => p.RefKind != RefKind.None))
                {
                    continue;
                }

                var sigKey = method.Name + "(" + string.Join(",", method.Parameters.Select(static p => p.Type.ToDisplayString(TypeKeyFormat))) + ")";
                if (seenSignatures.Add(sigKey))
                {
                    result.Add(method);
                }
            }
        }

        return result;
    }

    private static string? FindStringConstant(INamedTypeSymbol iface, string name)
    {
        foreach (var field in iface.GetMembers(name).OfType<IFieldSymbol>())
        {
            if (field.HasConstantValue && field.ConstantValue is string value)
            {
                return value;
            }
        }
        foreach (var inherited in iface.AllInterfaces)
        {
            foreach (var field in inherited.GetMembers(name).OfType<IFieldSymbol>())
            {
                if (field.HasConstantValue && field.ConstantValue is string value)
                {
                    return value;
                }
            }
        }
        return null;
    }

    private static string BuildToolName(string alias, string methodName)
    {
        var rawSegments = alias.Split(AliasSeparators, StringSplitOptions.RemoveEmptyEntries);
        var snakeSegments = new List<string>(rawSegments.Length);
        foreach (var segment in rawSegments)
        {
            var snake = ToSnakeCase(segment);
            if (snake.Length > 0)
            {
                snakeSegments.Add(snake);
            }
        }

        var capability = string.Join("_", snakeSegments);
        var methodSnake = ToSnakeCase(methodName);
        if (capability.Length == 0)
        {
            return methodSnake;
        }

        var lastSegment = snakeSegments[snakeSegments.Count - 1];
        if (string.Equals(lastSegment, methodSnake, StringComparison.Ordinal))
        {
            return capability;
        }

        return capability + "_" + methodSnake;
    }

    private static string ToSnakeCase(string input)
    {
        var sb = new StringBuilder(input.Length + 4);
        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (ch is '.' or '-' or '_' or ' ')
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != '_')
                {
                    sb.Append('_');
                }
                continue;
            }

            if (char.IsUpper(ch))
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != '_')
                {
                    var prev = input[i - 1];
                    var hasNextLower = i + 1 < input.Length && char.IsLower(input[i + 1]);
                    if (char.IsLower(prev) || char.IsDigit(prev) || (char.IsUpper(prev) && hasNextLower))
                    {
                        sb.Append('_');
                    }
                }
                sb.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        while (sb.Length > 0 && sb[sb.Length - 1] == '_')
        {
            sb.Length--;
        }
        return sb.ToString();
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }
        if (char.IsLower(name[0]))
        {
            return name;
        }
        if (name.Length == 1)
        {
            return name.ToLowerInvariant();
        }
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    private static string ResolveMethodDescription(List<IMethodSymbol> overloads, string methodName, string alias, CancellationToken cancellationToken)
    {
        foreach (var overload in overloads)
        {
            if (GetDescriptionAttribute(overload) is { } attr)
            {
                return attr;
            }
        }
        foreach (var overload in overloads)
        {
            if (GetXmlSummary(overload, cancellationToken) is { } summary)
            {
                return summary;
            }
        }
        foreach (var overload in overloads)
        {
            if (GetLeadingComment(overload, cancellationToken) is { } comment)
            {
                return comment;
            }
        }
        return methodName + " on " + alias + ".";
    }

    private static string? GetDescriptionAttribute(ISymbol symbol)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            var full = Rules.FullName(attr.AttributeClass);
            if (full == "System.ComponentModel.DescriptionAttribute"
                && attr.ConstructorArguments.Length > 0
                && attr.ConstructorArguments[0].Value is string text
                && !string.IsNullOrWhiteSpace(text))
            {
                return CollapseWhitespace(text);
            }
        }
        return null;
    }

    private static string? GetXmlSummary(ISymbol symbol, CancellationToken cancellationToken)
    {
        var xml = symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var root = XElement.Parse(xml, LoadOptions.PreserveWhitespace);
            var summary = root.Name.LocalName == "summary" ? root : root.Element("summary");
            if (summary is null)
            {
                return null;
            }

            var sb = new StringBuilder();
            ExtractXmlText(summary, sb);
            var text = CollapseWhitespace(sb.ToString());
            return text.Length > 0 ? text : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void ExtractXmlText(XElement element, StringBuilder sb)
    {
        foreach (var node in element.Nodes())
        {
            if (node is XText text)
            {
                sb.Append(text.Value);
            }
            else if (node is XElement child)
            {
                var local = child.Name.LocalName;
                if (local is "paramref" or "typeparamref")
                {
                    var name = child.Attribute("name")?.Value;
                    if (!string.IsNullOrEmpty(name))
                    {
                        sb.Append(name);
                    }
                }
                else if (local is "see" or "seealso")
                {
                    if (!string.IsNullOrWhiteSpace(child.Value))
                    {
                        ExtractXmlText(child, sb);
                    }
                    else if (child.Attribute("cref")?.Value is { Length: > 0 } cref)
                    {
                        var clean = cref.Length > 2 && cref[1] == ':' ? cref.Substring(2) : cref;
                        var paren = clean.IndexOf('(');
                        if (paren >= 0) { clean = clean.Substring(0, paren); }
                        var dot = clean.LastIndexOf('.');
                        sb.Append(dot >= 0 ? clean.Substring(dot + 1) : clean);
                    }
                }
                else
                {
                    if (local == "para" && sb.Length > 0)
                    {
                        sb.Append(' ');
                    }
                    ExtractXmlText(child, sb);
                }
            }
        }
    }

    private static string? GetLeadingComment(IMethodSymbol method, CancellationToken cancellationToken)
    {
        var reference = method.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null || reference.GetSyntax(cancellationToken) is not MethodDeclarationSyntax syntax)
        {
            return null;
        }

        var trivia = syntax.GetLeadingTrivia();
        var lines = new List<string>();
        var consecutiveEols = 0;
        for (var i = trivia.Count - 1; i >= 0; i--)
        {
            var item = trivia[i];
            if (item.IsKind(SyntaxKind.WhitespaceTrivia))
            {
                continue;
            }
            if (item.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                consecutiveEols++;
                if (consecutiveEols >= 2)
                {
                    break;
                }
                continue;
            }
            if (item.IsKind(SyntaxKind.SingleLineCommentTrivia))
            {
                consecutiveEols = 0;
                var raw = item.ToString();
                if (raw.StartsWith("//", StringComparison.Ordinal))
                {
                    raw = raw.Substring(2);
                }
                var trimmed = raw.Trim();
                if (trimmed.Length > 0)
                {
                    lines.Add(trimmed);
                }
                continue;
            }
            break;
        }

        if (lines.Count == 0)
        {
            return null;
        }

        lines.Reverse();
        return CollapseWhitespace(string.Join(" ", lines));
    }

    private static string CollapseWhitespace(string input)
    {
        var sb = new StringBuilder(input.Length);
        var inWhitespace = false;
        foreach (var ch in input)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0)
                {
                    inWhitespace = true;
                }
            }
            else
            {
                if (inWhitespace)
                {
                    sb.Append(' ');
                    inWhitespace = false;
                }
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    private static string BuildParametersJson(List<IMethodSymbol> overloads, string? defaultKey, string? keyPattern)
    {
        var properties = new List<(string Name, string SchemaJson)>();
        var seenProperties = new HashSet<string>(StringComparer.Ordinal);
        var required = new List<string>();

        if (defaultKey is null)
        {
            var keyDescription = !string.IsNullOrWhiteSpace(keyPattern)
                ? "Neuron key under your brain, e.g. " + keyPattern
                : "Neuron key under your brain.";
            var keySchema = "{\"type\":\"string\",\"description\":" + JsonString(keyDescription) + "}";
            properties.Add(("key", keySchema));
            seenProperties.Add("key");
            required.Add("key");
        }

        var requiredPerOverload = new List<HashSet<string>>(overloads.Count);
        foreach (var overload in overloads)
        {
            var overloadRequired = new HashSet<string>(StringComparer.Ordinal);
            foreach (var parameter in overload.Parameters)
            {
                if (IsCancellationToken(parameter.Type))
                {
                    continue;
                }

                var propName = ToCamelCase(parameter.Name);
                if (!IsOptionalParameter(parameter))
                {
                    overloadRequired.Add(propName);
                }

                if (seenProperties.Add(propName))
                {
                    var visiting = new HashSet<string>(StringComparer.Ordinal);
                    var paramDescription = GetDescriptionAttribute(parameter);
                    var hasDefault = parameter.HasExplicitDefaultValue;
                    var defaultValue = hasDefault ? parameter.ExplicitDefaultValue : null;
                    var schemaJson = BuildTypeSchemaJson(parameter.Type, paramDescription, hasDefault, defaultValue, depth: 0, visiting);
                    properties.Add((propName, schemaJson));
                }
            }
            requiredPerOverload.Add(overloadRequired);
        }

        foreach (var (name, _) in properties)
        {
            if (defaultKey is null && name == "key")
            {
                continue;
            }
            if (requiredPerOverload.All(set => set.Contains(name)))
            {
                required.Add(name);
            }
        }

        var sb = new StringBuilder();
        sb.Append("{\"type\":\"object\",\"properties\":{");
        for (var i = 0; i < properties.Count; i++)
        {
            if (i > 0) { sb.Append(','); }
            sb.Append(JsonString(properties[i].Name)).Append(':').Append(properties[i].SchemaJson);
        }
        sb.Append("},\"required\":[");
        for (var i = 0; i < required.Count; i++)
        {
            if (i > 0) { sb.Append(','); }
            sb.Append(JsonString(required[i]));
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string BuildTypeSchemaJson(
        ITypeSymbol rawType,
        string? description,
        bool hasExplicitDefault,
        object? explicitDefaultValue,
        int depth,
        HashSet<string> visiting)
    {
        var type = UnwrapNullable(rawType);
        var fields = new List<(string Key, string RawJson)>();

        if (type.SpecialType is SpecialType.System_String or SpecialType.System_Char)
        {
            fields.Add(("type", "\"string\""));
        }
        else if (type.SpecialType == SpecialType.System_Boolean)
        {
            fields.Add(("type", "\"boolean\""));
        }
        else if (type.SpecialType is SpecialType.System_SByte or SpecialType.System_Byte
            or SpecialType.System_Int16 or SpecialType.System_UInt16
            or SpecialType.System_Int32 or SpecialType.System_UInt32
            or SpecialType.System_Int64 or SpecialType.System_UInt64)
        {
            fields.Add(("type", "\"integer\""));
        }
        else if (type.SpecialType is SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal)
        {
            fields.Add(("type", "\"number\""));
        }
        else if (type.SpecialType == SpecialType.System_DateTime || IsFullName(type, "System.DateTimeOffset"))
        {
            fields.Add(("type", "\"string\""));
            fields.Add(("format", "\"date-time\""));
        }
        else if (IsFullName(type, "System.DateOnly"))
        {
            fields.Add(("type", "\"string\""));
            fields.Add(("format", "\"date\""));
        }
        else if (IsFullName(type, "System.TimeOnly"))
        {
            fields.Add(("type", "\"string\""));
            fields.Add(("format", "\"time\""));
        }
        else if (IsFullName(type, "System.Guid"))
        {
            fields.Add(("type", "\"string\""));
            fields.Add(("format", "\"uuid\""));
        }
        else if (IsFullName(type, "System.Uri"))
        {
            fields.Add(("type", "\"string\""));
            fields.Add(("format", "\"uri\""));
        }
        else if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType)
        {
            var members = enumType.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(static f => f.HasConstantValue && f.DeclaredAccessibility == Accessibility.Public)
                .ToArray();

            if (HasStringEnumConverter(enumType))
            {
                fields.Add(("type", "\"string\""));
                var names = string.Join(",", members.Select(static m => JsonString(m.Name)));
                fields.Add(("enum", "[" + names + "]"));
            }
            else
            {
                fields.Add(("type", "\"integer\""));
                var values = string.Join(",", members.Select(static m => FormatEnumInteger(m.ConstantValue)));
                fields.Add(("enum", "[" + values + "]"));
                var legend = string.Join(", ", members.Select(static m => FormatEnumInteger(m.ConstantValue) + " = " + m.Name));
                description = string.IsNullOrWhiteSpace(description) ? legend : description + " (" + legend + ")";
            }
        }
        else if (type is IArrayTypeSymbol arrayType)
        {
            fields.Add(("type", "\"array\""));
            fields.Add(("items", BuildTypeSchemaJson(arrayType.ElementType, description: null, hasExplicitDefault: false, explicitDefaultValue: null, depth, visiting)));
        }
        else if (TryGetSequenceElementType(type, out var elementType))
        {
            fields.Add(("type", "\"array\""));
            fields.Add(("items", BuildTypeSchemaJson(elementType!, description: null, hasExplicitDefault: false, explicitDefaultValue: null, depth, visiting)));
        }
        else if (TryGetStringDictionaryValueType(type, out var valueType))
        {
            fields.Add(("type", "\"object\""));
            fields.Add(("additionalProperties", BuildTypeSchemaJson(valueType!, description: null, hasExplicitDefault: false, explicitDefaultValue: null, depth + 1, visiting)));
        }
        else if (type is INamedTypeSymbol named && type.TypeKind is TypeKind.Class or TypeKind.Struct
            && type.SpecialType != SpecialType.System_Object
            && !IsOpaqueJsonType(type))
        {
            BuildObjectSchemaFields(named, depth, visiting, fields);
        }
        else
        {
            fields.Add(("type", "\"object\""));
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            fields.Add(("description", JsonString(description!)));
        }

        if (hasExplicitDefault && explicitDefaultValue is not null)
        {
            var defaultJson = FormatDefaultValue(type, explicitDefaultValue);
            if (defaultJson is not null)
            {
                fields.Add(("default", defaultJson));
            }
        }

        var sb = new StringBuilder();
        sb.Append('{');
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0) { sb.Append(','); }
            sb.Append(JsonString(fields[i].Key)).Append(':').Append(fields[i].RawJson);
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void BuildObjectSchemaFields(
        INamedTypeSymbol named,
        int depth,
        HashSet<string> visiting,
        List<(string Key, string RawJson)> fields)
    {
        fields.Add(("type", "\"object\""));

        var typeKey = named.ToDisplayString(TypeKeyFormat);
        if (depth >= MaxObjectDepth || !visiting.Add(typeKey))
        {
            return;
        }

        try
        {
            var primaryCtor = FindPrimaryOrBestConstructor(named);
            var props = new List<(string Name, string SchemaJson, bool Required)>();

            if (primaryCtor is not null && primaryCtor.Parameters.Length > 0)
            {
                var propsByName = named.GetMembers()
                    .OfType<IPropertySymbol>()
                    .Where(static p => !p.IsStatic)
                    .GroupBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(static g => g.Key, static g => g.First(), StringComparer.OrdinalIgnoreCase);

                foreach (var param in primaryCtor.Parameters)
                {
                    if (IsCancellationToken(param.Type))
                    {
                        continue;
                    }

                    var propName = ToCamelCase(param.Name);
                    var desc = GetDescriptionAttribute(param);
                    if (desc is null && propsByName.TryGetValue(param.Name, out var matchingProp))
                    {
                        desc = GetDescriptionAttribute(matchingProp);
                    }
                    var schema = BuildTypeSchemaJson(
                        param.Type,
                        desc,
                        param.HasExplicitDefaultValue,
                        param.HasExplicitDefaultValue ? param.ExplicitDefaultValue : null,
                        depth + 1,
                        visiting);
                    props.Add((propName, schema, !IsOptionalParameter(param)));
                }
            }
            else if (!named.IsAbstract)
            {
                foreach (var prop in named.GetMembers().OfType<IPropertySymbol>())
                {
                    if (prop.IsStatic
                        || prop.IsIndexer
                        || prop.DeclaredAccessibility != Accessibility.Public
                        || prop.GetMethod is null
                        || prop.SetMethod is null
                        || HasJsonIgnore(prop))
                    {
                        continue;
                    }

                    var propName = ToCamelCase(prop.Name);
                    var desc = GetDescriptionAttribute(prop);
                    var schema = BuildTypeSchemaJson(prop.Type, desc, hasExplicitDefault: false, explicitDefaultValue: null, depth + 1, visiting);
                    props.Add((propName, schema, !IsNullableType(prop.Type)));
                }
            }

            if (props.Count == 0)
            {
                return;
            }

            var propsSb = new StringBuilder();
            propsSb.Append('{');
            var req = new List<string>();
            for (var i = 0; i < props.Count; i++)
            {
                if (i > 0) { propsSb.Append(','); }
                propsSb.Append(JsonString(props[i].Name)).Append(':').Append(props[i].SchemaJson);
                if (props[i].Required)
                {
                    req.Add(props[i].Name);
                }
            }
            propsSb.Append('}');
            fields.Add(("properties", propsSb.ToString()));

            if (req.Count > 0)
            {
                fields.Add(("required", "[" + string.Join(",", req.Select(JsonString)) + "]"));
            }
        }
        finally
        {
            visiting.Remove(typeKey);
        }
    }

    private static IMethodSymbol? FindPrimaryOrBestConstructor(INamedTypeSymbol named)
    {
        if (named.IsAbstract)
        {
            return null;
        }

        // 1. Primary constructor declared on the type declaration syntax (records, C# 12+ primary ctors).
        foreach (var ctor in named.InstanceConstructors)
        {
            if (ctor.DeclaredAccessibility != Accessibility.Public || ctor.Parameters.Length == 0)
            {
                continue;
            }
            foreach (var syntaxRef in ctor.DeclaringSyntaxReferences)
            {
                if (syntaxRef.GetSyntax() is TypeDeclarationSyntax)
                {
                    return ctor;
                }
            }
        }

        // 2. Fallback (e.g. metadata-referenced records/classes): public instance constructor with the most parameters,
        //    excluding compiler-synthesized record copy constructors.
        IMethodSymbol? best = null;
        foreach (var ctor in named.InstanceConstructors)
        {
            if (ctor.DeclaredAccessibility != Accessibility.Public || ctor.Parameters.Length == 0)
            {
                continue;
            }
            if (ctor.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, named))
            {
                continue;
            }
            if (best is null || ctor.Parameters.Length > best.Parameters.Length)
            {
                best = ctor;
            }
        }
        return best;
    }

    private static bool HasJsonIgnore(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (Rules.FullName(attr.AttributeClass) == "System.Text.Json.Serialization.JsonIgnoreAttribute")
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasStringEnumConverter(INamedTypeSymbol enumType)
    {
        foreach (var attr in enumType.GetAttributes())
        {
            if (Rules.FullName(attr.AttributeClass) != "System.Text.Json.Serialization.JsonConverterAttribute")
            {
                continue;
            }
            if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is INamedTypeSymbol converterType)
            {
                for (var cur = converterType; cur is not null; cur = cur.BaseType)
                {
                    var full = Rules.FullName(cur.OriginalDefinition);
                    if (full is "System.Text.Json.Serialization.JsonStringEnumConverter" or "System.Text.Json.Serialization.JsonStringEnumConverter`1")
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private static bool TryGetSequenceElementType(ITypeSymbol type, out ITypeSymbol? elementType)
    {
        if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named)
        {
            var full = Rules.FullName(named.OriginalDefinition);
            if (full is "System.Collections.Generic.IReadOnlyList`1"
                or "System.Collections.Generic.IList`1"
                or "System.Collections.Generic.List`1"
                or "System.Collections.Generic.IReadOnlyCollection`1"
                or "System.Collections.Generic.ICollection`1"
                or "System.Collections.Generic.IEnumerable`1"
                or "System.Collections.Generic.ISet`1"
                or "System.Collections.Generic.IReadOnlySet`1"
                or "System.Collections.Generic.HashSet`1"
                or "System.Collections.Immutable.ImmutableArray`1"
                or "System.Collections.Immutable.ImmutableList`1"
                or "System.Collections.Immutable.ImmutableHashSet`1")
            {
                elementType = named.TypeArguments[0];
                return true;
            }
        }
        elementType = null;
        return false;
    }

    private static bool TryGetStringDictionaryValueType(ITypeSymbol type, out ITypeSymbol? valueType)
    {
        if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 2 } named
            && named.TypeArguments[0].SpecialType == SpecialType.System_String)
        {
            var full = Rules.FullName(named.OriginalDefinition);
            if (full is "System.Collections.Generic.IDictionary`2"
                or "System.Collections.Generic.IReadOnlyDictionary`2"
                or "System.Collections.Generic.Dictionary`2"
                or "System.Collections.Immutable.ImmutableDictionary`2")
            {
                valueType = named.TypeArguments[1];
                return true;
            }
        }
        valueType = null;
        return false;
    }

    private static bool IsOpaqueJsonType(ITypeSymbol type)
    {
        var full = type is INamedTypeSymbol named ? Rules.FullName(named.OriginalDefinition) : string.Empty;
        return full is "System.Text.Json.JsonElement"
            or "System.Text.Json.JsonDocument"
            or "System.Text.Json.Nodes.JsonNode"
            or "System.Text.Json.Nodes.JsonObject"
            or "System.Text.Json.Nodes.JsonArray"
            or "System.Text.Json.Nodes.JsonValue";
    }

    private static bool IsCancellationToken(ITypeSymbol type)
        => IsFullName(UnwrapNullable(type), "System.Threading.CancellationToken");

    private static bool IsOptionalParameter(IParameterSymbol parameter)
        => parameter.HasExplicitDefaultValue || IsNullableType(parameter.Type);

    private static bool IsNullableType(ITypeSymbol type)
        => type.NullableAnnotation == NullableAnnotation.Annotated
           || (type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T);

    private static ITypeSymbol UnwrapNullable(ITypeSymbol type)
        => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments.Length: 1 } named
            ? named.TypeArguments[0]
            : type;

    private static bool IsFullName(ITypeSymbol type, string fullName)
        => type is INamedTypeSymbol named && Rules.FullName(named) == fullName;

    private static string FormatEnumInteger(object? value)
        => value switch
        {
            ulong u => u.ToString(CultureInfo.InvariantCulture),
            null => "0",
            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        };

    private static string? FormatDefaultValue(ITypeSymbol unwrappedType, object value)
    {
        if (unwrappedType.TypeKind == TypeKind.Enum && unwrappedType is INamedTypeSymbol enumType)
        {
            var numeric = FormatEnumInteger(value);
            if (HasStringEnumConverter(enumType))
            {
                foreach (var field in enumType.GetMembers().OfType<IFieldSymbol>())
                {
                    if (field.HasConstantValue && FormatEnumInteger(field.ConstantValue) == numeric)
                    {
                        return JsonString(field.Name);
                    }
                }
            }
            return numeric;
        }

        return value switch
        {
            string s => JsonString(s),
            char c => JsonString(c.ToString()),
            bool b => b ? "true" : "false",
            sbyte v => v.ToString(CultureInfo.InvariantCulture),
            byte v => v.ToString(CultureInfo.InvariantCulture),
            short v => v.ToString(CultureInfo.InvariantCulture),
            ushort v => v.ToString(CultureInfo.InvariantCulture),
            int v => v.ToString(CultureInfo.InvariantCulture),
            uint v => v.ToString(CultureInfo.InvariantCulture),
            long v => v.ToString(CultureInfo.InvariantCulture),
            ulong v => v.ToString(CultureInfo.InvariantCulture),
            float v when !float.IsNaN(v) && !float.IsInfinity(v) => v.ToString("G9", CultureInfo.InvariantCulture),
            double v when !double.IsNaN(v) && !double.IsInfinity(v) => v.ToString("G17", CultureInfo.InvariantCulture),
            decimal v => v.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static string ClrFullName(INamedTypeSymbol type)
    {
        var names = new Stack<string>();
        for (var cur = type; cur is not null; cur = cur.ContainingType)
        {
            names.Push(cur.MetadataName);
        }
        var nested = string.Join("+", names);
        return type.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.ToDisplayString() + "." + nested
            : nested;
    }

    private static string JsonString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20)
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string CSharpLiteral(string? value)
        => value is null ? "null" : SymbolDisplay.FormatLiteral(value, quote: true);

    private static string SanitizeIdentifier(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }
        return sb.ToString();
    }

    private static void Emit(SourceProductionContext context, ImmutableArray<ContractModel> contracts, bool hasToolSchemas, string assemblyName)
    {
        if (contracts.IsDefaultOrEmpty)
        {
            return;
        }

        var distinct = contracts
            .GroupBy(static c => c.ContractFullName, StringComparer.Ordinal)
            .Select(static g => g.First())
            .OrderBy(static c => c.ContractFullName, StringComparer.Ordinal)
            .ToArray();

        foreach (var contract in distinct)
        {
            foreach (var diag in contract.Diagnostics)
            {
                var location = !string.IsNullOrEmpty(diag.FilePath)
                    ? Location.Create(diag.FilePath, diag.Span, diag.LineSpan)
                    : Location.None;
                context.ReportDiagnostic(Diagnostic.Create(
                    Rules.ToolNameTooLong,
                    location,
                    diag.ToolName,
                    diag.ContractName,
                    diag.MethodName,
                    diag.ToolName.Length));
            }
        }

        if (!hasToolSchemas)
        {
            return;
        }

        var allSchemas = distinct.SelectMany(static c => c.Schemas).ToArray();
        if (allSchemas.Length == 0)
        {
            return;
        }

        var className = "ToolSchemaRegistration_" + SanitizeIdentifier(assemblyName);
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CA2255");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine();
        sb.AppendLine("namespace DigitalBrain.Generated;");
        sb.AppendLine();
        sb.AppendLine("[CompilerGenerated]");
        sb.Append("internal static class ").AppendLine(className);
        sb.AppendLine("{");
        sb.AppendLine("    [ModuleInitializer]");
        sb.AppendLine("    internal static void Initialize()");
        sb.AppendLine("    {");
        sb.AppendLine("        global::DigitalBrain.Contracts.ToolSchemas.Register(new global::DigitalBrain.Contracts.ToolSchema[]");
        sb.AppendLine("        {");
        foreach (var schema in allSchemas)
        {
            sb.Append("            new global::DigitalBrain.Contracts.ToolSchema(")
                .Append(CSharpLiteral(schema.Contract)).Append(", ")
                .Append(CSharpLiteral(schema.Name)).Append(", ")
                .Append(CSharpLiteral(schema.Method)).Append(", ")
                .Append(CSharpLiteral(schema.Description)).Append(", ")
                .Append(CSharpLiteral(schema.ParametersJson)).Append(", ")
                .Append(CSharpLiteral(schema.DefaultKey)).Append(", ")
                .Append(CSharpLiteral(schema.KeyPattern)).Append(", ")
                .Append(schema.ReadOnly ? "true" : "false")
                .AppendLine("),");
        }
        sb.AppendLine("        });");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        context.AddSource("DigitalBrain.ToolSchemas.g.cs", sb.ToString());
    }

    private sealed class ContractModel : IEquatable<ContractModel>
    {
        public ContractModel(string contractFullName, ImmutableArray<SchemaModel> schemas, ImmutableArray<DiagnosticModel> diagnostics)
        {
            ContractFullName = contractFullName;
            Schemas = schemas;
            Diagnostics = diagnostics;
        }

        public string ContractFullName { get; }
        public ImmutableArray<SchemaModel> Schemas { get; }
        public ImmutableArray<DiagnosticModel> Diagnostics { get; }

        public bool Equals(ContractModel? other)
        {
            if (other is null || !string.Equals(ContractFullName, other.ContractFullName, StringComparison.Ordinal))
            {
                return false;
            }
            if (Schemas.Length != other.Schemas.Length || Diagnostics.Length != other.Diagnostics.Length)
            {
                return false;
            }
            for (var i = 0; i < Schemas.Length; i++)
            {
                if (!Schemas[i].Equals(other.Schemas[i])) { return false; }
            }
            for (var i = 0; i < Diagnostics.Length; i++)
            {
                if (!Diagnostics[i].Equals(other.Diagnostics[i])) { return false; }
            }
            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as ContractModel);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ContractFullName);
    }

    private sealed class SchemaModel : IEquatable<SchemaModel>
    {
        public SchemaModel(
            string contract,
            string name,
            string method,
            string description,
            string parametersJson,
            string? defaultKey,
            string? keyPattern,
            bool readOnly)
        {
            Contract = contract;
            Name = name;
            Method = method;
            Description = description;
            ParametersJson = parametersJson;
            DefaultKey = defaultKey;
            KeyPattern = keyPattern;
            ReadOnly = readOnly;
        }

        public string Contract { get; }
        public string Name { get; }
        public string Method { get; }
        public string Description { get; }
        public string ParametersJson { get; }
        public string? DefaultKey { get; }
        public string? KeyPattern { get; }
        public bool ReadOnly { get; }

        public bool Equals(SchemaModel? other)
            => other is not null
               && string.Equals(Contract, other.Contract, StringComparison.Ordinal)
               && string.Equals(Name, other.Name, StringComparison.Ordinal)
               && string.Equals(Method, other.Method, StringComparison.Ordinal)
               && string.Equals(Description, other.Description, StringComparison.Ordinal)
               && string.Equals(ParametersJson, other.ParametersJson, StringComparison.Ordinal)
               && string.Equals(DefaultKey, other.DefaultKey, StringComparison.Ordinal)
               && string.Equals(KeyPattern, other.KeyPattern, StringComparison.Ordinal)
               && ReadOnly == other.ReadOnly;

        public override bool Equals(object? obj) => Equals(obj as SchemaModel);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(Contract);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(Name);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(ParametersJson);
                return hash;
            }
        }
    }

    private sealed class DiagnosticModel : IEquatable<DiagnosticModel>
    {
        public DiagnosticModel(string filePath, TextSpan span, LinePositionSpan lineSpan, string toolName, string contractName, string methodName)
        {
            FilePath = filePath;
            Span = span;
            LineSpan = lineSpan;
            ToolName = toolName;
            ContractName = contractName;
            MethodName = methodName;
        }

        public string FilePath { get; }
        public TextSpan Span { get; }
        public LinePositionSpan LineSpan { get; }
        public string ToolName { get; }
        public string ContractName { get; }
        public string MethodName { get; }

        public bool Equals(DiagnosticModel? other)
            => other is not null
               && string.Equals(FilePath, other.FilePath, StringComparison.Ordinal)
               && Span.Equals(other.Span)
               && LineSpan.Equals(other.LineSpan)
               && string.Equals(ToolName, other.ToolName, StringComparison.Ordinal)
               && string.Equals(ContractName, other.ContractName, StringComparison.Ordinal)
               && string.Equals(MethodName, other.MethodName, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as DiagnosticModel);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ToolName);
    }
}
