using System;
using System.Collections.Immutable;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DigitalBrain.Analyzers.Apps;

/// <summary>
/// DB0027: an app type has a spec comment: a leading <c>/* ... */</c> comment in a file that declares it, or an
/// AdditionalFiles item named <c>&lt;Type&gt;.spec.md</c> (or <c>&lt;file&gt;.spec.md</c> next to a script <c>app.cs</c>).
/// Whether the comment agrees with the manifest is DB0024 (08-03).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SpecCommentAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(AppRules.MissingSpec);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(Analyze, SymbolKind.NamedType);
    }

    private static void Analyze(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (!AppRules.IsApp(type) || type.IsAbstract) { return; }

        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            var root = reference.SyntaxTree.GetRoot(context.CancellationToken);
            foreach (var trivia in root.GetLeadingTrivia())
            {
                if (trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)) { return; }
            }
            var declaration = reference.GetSyntax(context.CancellationToken);
            foreach (var trivia in declaration.GetLeadingTrivia())
            {
                if (trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)) { return; }
            }
        }

        foreach (var file in context.Options.AdditionalFiles)
        {
            var name = Path.GetFileName(file.Path);
            if (string.Equals(name, type.Name + ".spec.md", StringComparison.OrdinalIgnoreCase)) { return; }
            foreach (var reference in type.DeclaringSyntaxReferences)
            {
                var path = reference.SyntaxTree.FilePath;
                if (string.IsNullOrEmpty(path)) { continue; }
                if (string.Equals(name, Path.GetFileNameWithoutExtension(path) + ".spec.md", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetDirectoryName(file.Path), Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        context.ReportDiagnostic(Diagnostic.Create(AppRules.MissingSpec, type.Locations[0], type.Name));
    }
}
