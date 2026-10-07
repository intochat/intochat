using System.Reflection;
using DigitalBrain.Flutter.Form;
using DigitalBrain.Flutter.TextField;
using DigitalBrain.Framework;

namespace DigitalBrain.Modules.Flutter.Tests.Composition;

public sealed class TypeParityFacts
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

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

        var renderer = File.ReadAllText(PathInRepo("src/DigitalBrain.Modules/Google/Flutter/app/ui/lib/src/composition/neuron_view.dart"));
        Assert.Contains($"obscureText: kind == '{secretKind}' || kind == 'password'", renderer);
    }

    private static string PathInRepo(string relative) =>
        Path.Combine(RepositoryRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DigitalBrain.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException($"Repository root (DigitalBrain.slnx) was not found above {AppContext.BaseDirectory}.");
    }
}
