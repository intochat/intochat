using System.Reflection;
using IntoChat.OS.Flutter.Form;
using IntoChat.OS.Flutter.TextField;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Composition;

public sealed class TypeParityFacts
{
    private static readonly string FlutterApp = FindFlutterApp();

    [Fact]
    public void CatalogRegistersEveryFieldKindExactlyOnce()
    {
        var kinds = Enum.GetValues<FieldKind>();
        Assert.Equal(kinds.Length, TypeCatalog.All.Count);
        Assert.Equal(kinds.Length, TypeCatalog.All.Select(type => type.Kind).Distinct().Count());
        foreach (var kind in kinds)
        {
            Assert.Equal(kind, TypeCatalog.Get(kind).Kind);
        }
    }

    [Fact]
    public void TheFormFieldKindsMirrorTheFrameworkCatalogKinds()
    {
        Assert.Equal(Enum.GetNames<FieldKind>(), Enum.GetNames<FormFieldKind>());
        Assert.Equal(Enum.GetValues<FieldKind>().Select(kind => (int)kind), Enum.GetValues<FormFieldKind>().Select(kind => (int)kind));
    }

    [Fact]
    public void MaskedKindParityHoldsBetweenTheContractTheCatalogAndTheFlutterRenderer()
    {
        var secretKind = TypeCatalog.Get(FieldKind.Secret).Id;

        var accepted = (IEnumerable<string>)typeof(TextFieldNeuron)
            .GetField("Kinds", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        Assert.Contains(secretKind, accepted);

        var renderer = File.ReadAllText(Path.Combine(FlutterApp, "ui/lib/src/composition/neuron_view.dart".Replace('/', Path.DirectorySeparatorChar)));
        Assert.Contains($"secret: kind == '{secretKind}' || kind == 'password'", renderer);
    }

    // The Flutter workspace sits beside the module project (app/); the project is found through
    // the solution by name, never by a path literal.
    private static string FindFlutterApp()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Intochat.OS.slnx")))
        {
            directory = directory.Parent;
        }
        var root = directory?.FullName
            ?? throw new InvalidOperationException($"Repository root (Intochat.OS.slnx) was not found above {AppContext.BaseDirectory}.");
        var module = System.Xml.Linq.XDocument.Load(Path.Combine(root, "Intochat.OS.slnx")).Descendants("Project")
            .Select(element => element.Attribute("Path")!.Value.Replace('\\', '/'))
            .Single(path => Path.GetFileNameWithoutExtension(path) == "IntoChat.OS.Modules.Flutter");
        return Path.GetFullPath(Path.Combine(root, Path.GetDirectoryName(module)!, "..", "app"));
    }
}
