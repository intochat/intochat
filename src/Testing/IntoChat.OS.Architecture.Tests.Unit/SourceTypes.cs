using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntoChat.OS.Architecture.Tests.Unit;

// A syntax-only map of every class and interface declared in hand-written source of every solution project, tests
// included (ProductionArchitecture loads production assemblies only, and the neuron ratchets must see test and
// fixture neurons too). Names follow reflection: namespace, '+' between nesting levels, `arity for generics.
// Inheritance is resolved by simple name; SourceTypesAgreeWithReflection holds that against the loaded assemblies.
internal static class SourceTypes
{
    internal sealed record Declared(string File, string Project, string Namespace, string FullName, string Name, bool IsInterface, bool IsAbstract,
        string[] Bases, AttributeSyntax[] Attributes, ParameterSyntax[] StateParameters);

    internal static readonly string[] GrainInterfaceRoots =
        ["IGrain", "IGrainWithStringKey", "IGrainWithIntegerKey", "IGrainWithGuidKey", "IGrainWithGuidCompoundKey", "IGrainWithIntegerCompoundKey"];

    internal static readonly Lazy<Declared[]> All = new(Scan);

    // Classes deriving Orleans Grain directly or through any scanned base (the os Framework Neuron among them).
    // A base written as DigitalBrain.* is the plain-class core, never a grain.
    internal static readonly Lazy<Declared[]> Grains = new(() => Closure(All.Value.Where(type => !type.IsInterface), ["Grain"]));

    internal static readonly Lazy<Declared[]> GrainInterfaces = new(() => Closure(All.Value.Where(type => type.IsInterface), GrainInterfaceRoots));

    // "Type.Member" -> literal for every `const string` in scanned source, so attribute arguments resolve without a compilation.
    internal static readonly Lazy<Dictionary<string, string>> Constants = new(() =>
    {
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Files())
        {
            foreach (var field in CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>()
                .Where(field => field.Modifiers.Any(SyntaxKind.ConstKeyword)))
            {
                var owner = field.Parent as BaseTypeDeclarationSyntax;
                foreach (var variable in field.Declaration.Variables)
                {
                    if (owner is not null && variable.Initializer?.Value is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal)
                    {
                        constants.TryAdd(owner.Identifier.Text + "." + variable.Identifier.Text, literal.Token.ValueText);
                    }
                }
            }
        }
        return constants;
    });

    internal static string? Resolve(ExpressionSyntax expression, Declared owner) => expression switch
    {
        LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal => literal.Token.ValueText,
        LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } => null,
        MemberAccessExpressionSyntax access => Constants.Value.GetValueOrDefault(Last(access.Expression) + "." + access.Name.Identifier.Text),
        IdentifierNameSyntax name => Constants.Value.GetValueOrDefault(owner.Name.Split('`')[0] + "." + name.Identifier.Text),
        _ => null,
    };

    private static string Last(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.Text,
        SimpleNameSyntax name => name.Identifier.Text,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.Text,
        _ => expression.ToString(),
    };

    private static Declared[] Closure(IEnumerable<Declared> candidates, string[] roots)
    {
        var pool = candidates.ToArray();
        var names = roots.ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, Declared>(StringComparer.Ordinal);
        bool grew;
        do
        {
            grew = false;
            foreach (var type in pool.Where(type => !found.ContainsKey(type.FullName) && type.Bases.Any(names.Contains)))
            {
                found[type.FullName] = type;
                names.Add(type.Name.Split('`')[0]);
                grew = true;
            }
        }
        while (grew);
        return [.. found.Values.OrderBy(type => type.FullName, StringComparer.Ordinal)];
    }

    private static IEnumerable<string> Files()
        => Repository.ProjectFiles("*.cs", path => !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase);

    private static Declared[] Scan()
    {
        var byName = new Dictionary<string, Declared>(StringComparer.Ordinal);
        foreach (var path in Files())
        {
            var key = Repository.ProjectKey(path);
            var project = key[..key.IndexOf('/', StringComparison.Ordinal)];
            foreach (var node in CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>()
                .Where(node => node is ClassDeclarationSyntax or InterfaceDeclarationSyntax or RecordDeclarationSyntax))
            {
                var ns = string.Join('.', node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(declaration => declaration.Name.ToString()));
                var nesting = node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Reverse()
                    .Select(type => type.Identifier.Text + (type.TypeParameterList is { } list ? "`" + list.Parameters.Count : ""));
                var fullName = (ns.Length > 0 ? ns + "." : "") + string.Join('+', nesting);
                var bases = node.BaseList?.Types.Select(type => BaseName(type.Type)).OfType<string>().ToArray() ?? [];
                var attributes = node.AttributeLists.SelectMany(list => list.Attributes).ToArray();
                var parameters = node.ParameterList?.Parameters.AsEnumerable() ?? [];
                parameters = parameters.Concat(node.Members.OfType<ConstructorDeclarationSyntax>().SelectMany(constructor => constructor.ParameterList.Parameters));
                var state = parameters.Where(parameter => parameter.AttributeLists.SelectMany(list => list.Attributes).Any(IsPersistentState)).ToArray();
                var declared = new Declared(key, project, ns, fullName, node.Identifier.Text + (node.TypeParameterList is { } own ? "`" + own.Parameters.Count : ""),
                    node is InterfaceDeclarationSyntax, node.Modifiers.Any(SyntaxKind.AbstractKeyword), bases, attributes, state);
                byName[fullName] = byName.TryGetValue(fullName, out var partial)
                    ? partial with
                    {
                        Bases = [.. partial.Bases.Union(bases)],
                        Attributes = [.. partial.Attributes, .. attributes],
                        StateParameters = [.. partial.StateParameters, .. state],
                        IsAbstract = partial.IsAbstract || declared.IsAbstract,
                        File = partial.Bases.Length > 0 || bases.Length == 0 ? partial.File : key,
                    }
                    : declared;
            }
        }
        return [.. byName.Values];
    }

    internal static bool IsPersistentState(AttributeSyntax attribute) => AttributeName(attribute) == "PersistentState";

    internal static string AttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.Text,
            SimpleNameSyntax simple => simple.Identifier.Text,
            _ => attribute.Name.ToString(),
        };
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    internal static string AttributeNamespace(AttributeSyntax attribute)
        => attribute.Name is QualifiedNameSyntax qualified ? qualified.Left.ToString() : "";

    private static string? BaseName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified when IsDigitalBrain(qualified.Left) => null,
        QualifiedNameSyntax qualified => BaseName(qualified.Right),
        AliasQualifiedNameSyntax alias => BaseName(alias.Name),
        GenericNameSyntax generic => generic.Identifier.Text,
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        _ => null,
    };

    // DigitalBrain.X, or X through a file's alias of a DigitalBrain namespace (using DB = DigitalBrain;).
    private static bool IsDigitalBrain(NameSyntax left)
    {
        if (left.ToString().StartsWith("DigitalBrain", StringComparison.Ordinal)) { return true; }
        return left is IdentifierNameSyntax alias && left.SyntaxTree.GetRoot() is CompilationUnitSyntax root
            && root.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(directive => directive.Alias?.Name.Identifier.Text == alias.Identifier.Text
                && directive.NamespaceOrType.ToString().StartsWith("DigitalBrain", StringComparison.Ordinal));
    }
}
