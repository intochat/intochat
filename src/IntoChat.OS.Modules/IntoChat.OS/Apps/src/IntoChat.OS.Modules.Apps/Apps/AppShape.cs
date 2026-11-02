using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntoChat.OS.Modules.Apps;

// Reads an app's operations from its source, without loading it: every [On] method of the [App] class
// (across partial files) that is not a signal reaction ([On("source")]). Names follow the sandbox runner's
// kebab-case rule, so a derived name always resolves to its method.
internal static class AppShape
{
    public static AppOperation[] Operations(IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var operations = new List<AppOperation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, source) in files.Where(file => file.Key.EndsWith(".cs", StringComparison.Ordinal)).OrderBy(file => file.Key, StringComparer.Ordinal))
        {
            var root = CSharpSyntaxTree.ParseText(source ?? "", path: path).GetCompilationUnitRoot();
            foreach (var type in root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(IsApp))
            {
                foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
                {
                    if (OnAttribute(method) is not { } on || IsReaction(on)) { continue; }
                    var name = Kebab(method.Identifier.ValueText);
                    if (seen.Add(name)) { operations.Add(new AppOperation(name, Description(method))); }
                }
            }
        }
        return [.. operations];
    }

    // Derived operations are the authority; a declared description is kept for an operation the code has.
    public static AppOperation[] Merge(AppOperation[] derived, IEnumerable<AppOperation>? declared)
    {
        var descriptions = (declared ?? []).GroupBy(operation => OperationKey(operation.Name), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Description, StringComparer.Ordinal);
        return [.. derived.Select(operation => descriptions.TryGetValue(OperationKey(operation.Name), out var described) && described.Length > 0
            ? operation with { Description = described } : operation)];
    }

    internal static string Kebab(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]) ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private static string OperationKey(string name) => name.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static bool IsApp(ClassDeclarationSyntax type) =>
        type.AttributeLists.SelectMany(list => list.Attributes).Any(attribute => Named(attribute, "App"))
        || type.Modifiers.Any(SyntaxKind.PartialKeyword) && type.BaseList is null && type.Members.OfType<MethodDeclarationSyntax>().Any(method => OnAttribute(method) is not null);

    private static AttributeSyntax? OnAttribute(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(list => list.Attributes).FirstOrDefault(attribute => Named(attribute, "On"));

    // A positional argument is the source key of a signal reaction.
    private static bool IsReaction(AttributeSyntax on) =>
        on.ArgumentList?.Arguments.Any(argument => argument.NameEquals is null && argument.NameColon is null) == true;

    private static bool Named(AttributeSyntax attribute, string name)
    {
        var simple = attribute.Name switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            SimpleNameSyntax plain => plain.Identifier.ValueText,
            _ => "",
        };
        return simple == name || simple == name + "Attribute";
    }

    private static string Description(MethodDeclarationSyntax method)
    {
        var lines = method.GetLeadingTrivia()
            .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
            .SelectMany(trivia => trivia.ToFullString().Split('\n'))
            .Select(line => line.Trim().TrimStart('/').Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('<'));
        return string.Join(" ", lines);
    }
}
