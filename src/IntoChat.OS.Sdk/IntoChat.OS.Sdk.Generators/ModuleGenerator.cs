using System.Collections.Immutable;
using IntoChat.OS.Sdk.Generators.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace IntoChat.OS.Sdk.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class ModuleGenerator : IIncrementalGenerator
{
    private const string ModuleAttribute = "IntoChat.OS.Sdk.ModuleAttribute";
    private const string HostingAttribute = "IntoChat.OS.Sdk.ModuleHostingAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var modules = context.SyntaxProvider.ForAttributeWithMetadataName(
                ModuleAttribute,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, ct) => ModuleParser.Module(ctx, ct))
            .WithTrackingName("ModuleModel");
        context.RegisterSourceOutput(modules, static (spc, model) =>
        {
            foreach (var diagnostic in model.Diagnostics) { Report(spc, diagnostic); }
            if (model.Diagnostics.Length == 0) { spc.AddSource(HintName(model.Namespace, model.ClassName), SourceText.From(ModuleEmitter.Emit(model), System.Text.Encoding.UTF8)); }
        });

        var hosting = context.SyntaxProvider.ForAttributeWithMetadataName(
                HostingAttribute,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, ct) => ModuleParser.Hosting(ctx))
            .WithTrackingName("HostingModel");
        context.RegisterSourceOutput(hosting, static (spc, model) =>
        {
            foreach (var diagnostic in model.Diagnostics) { Report(spc, diagnostic); }
            if (model.Diagnostics.Length == 0) { spc.AddSource(HintName(model.Namespace, model.ClassName), SourceText.From(HostingEmitter.Emit(model), System.Text.Encoding.UTF8)); }
        });
    }

    private static string HintName(string? ns, string className) => (ns is null ? className : ns + "." + className) + ".g.cs";

    private static void Report(SourceProductionContext context, DiagnosticModel model)
    {
        var location = Location.Create(model.FilePath, new TextSpan(model.Start, model.Length),
            new LinePositionSpan(new LinePosition(model.Line, model.Character), new LinePosition(model.Line, model.Character + model.Length)));
        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ById(model.Id), location, model.Argument));
    }
}
