using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntoChat.OS.Apps;

// The types a feature brings, read from syntax: public records and classes deriving from Signal in
// its shared files. Neurons a script defines are not types yet (a deferred fork of the kernel design).
internal static class FeatureTypes
{
    private static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);

    public static TypeEntry[] Of(IReadOnlyDictionary<string, string> files, string provider)
        => [.. files.Where(file => AppFiles.IsProgram(file.Key) && file.Key != AppFiles.DeclarationPath)
            .SelectMany(file => CSharpSyntaxTree.ParseText(file.Value, Parse).GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            .Where(type => type.Modifiers.Any(SyntaxKind.PublicKeyword)
                && type.BaseList?.Types.Any(baseType => baseType.Type.ToString() is "Signal" or "IntoChat.OS.Signal") == true)
            .Select(type => new TypeEntry(FullName(type), type.Identifier.Text, TypeKind.Signal, provider, "",
                [.. Properties(type)], []))
            .DistinctBy(entry => entry.Name)];

    private static string FullName(TypeDeclarationSyntax type)
    {
        var names = type.Ancestors().OfType<BaseTypeDeclarationSyntax>().Select(parent => parent.Identifier.Text).Reverse().Append(type.Identifier.Text);
        var space = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Select(ns => ns.Name.ToString()).Reverse().ToArray()
            is { Length: > 0 } spaces ? string.Join('.', spaces) + "." : "";
        return space + string.Join('.', names);
    }

    private static IEnumerable<string> Properties(TypeDeclarationSyntax type)
        => (type.ParameterList?.Parameters.Select(parameter => $"{parameter.Type} {parameter.Identifier.Text}") ?? [])
            .Concat(type.Members.OfType<PropertyDeclarationSyntax>().Where(property => property.Modifiers.Any(SyntaxKind.PublicKeyword))
                .Select(property => $"{property.Type} {property.Identifier.Text}"));
}
