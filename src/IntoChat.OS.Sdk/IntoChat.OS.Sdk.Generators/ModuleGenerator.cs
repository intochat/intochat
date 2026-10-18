using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace IntoChat.OS.Sdk.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class ModuleGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var modules = context.SyntaxProvider.ForAttributeWithMetadataName(
                "IntoChat.OS.Sdk.ModuleAttribute",
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => ctx.TargetSymbol.ToDisplayString())
            .WithTrackingName("ModuleModel");
        context.RegisterSourceOutput(modules, static (spc, name) =>
            spc.AddSource(name + ".g.cs", SourceText.From("// module: " + name + "\n", Encoding.UTF8)));
    }
}
