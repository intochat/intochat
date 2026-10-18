using System.Reflection;
using System.Text.RegularExpressions;
using Orleans.Metadata;

namespace IntoChat.OS.Architecture.Tests.Unit;

// RENAME-01: every persisted or wire identity carries its own rename-safe, brand-neutral alias, so a later
// namespace or product rename cannot re-identify stored data. Scope is ProductionArchitecture.Types, the same
// scope as ContractManifest. Brand tokens are built from split literals so this file never matches the brand gate.
public sealed partial class AliasFacts
{
    private static readonly string[] BrandTokens = ["digital" + "brain", "into" + "chat"];

    [GeneratedRegex(@"\.[A-Z][A-Za-z]+\.")]
    private static partial Regex NamespaceLike();

    private static IEnumerable<Type> Identified() => ProductionArchitecture.Types.Where(type =>
        type.IsDefined(typeof(GenerateSerializerAttribute), inherit: false)
        || (type.IsInterface && typeof(IGrain).IsAssignableFrom(type)));

    private static string? OwnAlias(Type type)
        => type.GetCustomAttribute<NeuronAliasAttribute>(inherit: false)?.Alias
            ?? type.GetCustomAttribute<AliasAttribute>(inherit: false)?.Alias;

    private static bool HasBrand(string value)
        => BrandTokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void EverySerializableTypeAndGrainInterfaceHasItsOwnNeutralAlias()
    {
        var offenders = Identified()
            .Select(type => (Type: type, Alias: OwnAlias(type)))
            .Where(entry => entry.Alias is null || HasBrand(entry.Alias) || NamespaceLike().IsMatch(entry.Alias))
            .Select(entry => $"{entry.Type.FullName} -> {entry.Alias ?? "(no alias)"}")
            .Order(StringComparer.Ordinal).ToArray();
        Assert.True(offenders.Length == 0, "Types without a neutral own alias:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void AliasesAreUniqueAcrossProductionTypes()
    {
        var duplicates = Identified()
            .Select(type => (Type: type, Alias: OwnAlias(type)))
            .Where(entry => entry.Alias is not null)
            .GroupBy(entry => entry.Alias!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(entry => entry.Type.FullName))}")
            .Order(StringComparer.Ordinal).ToArray();
        Assert.True(duplicates.Length == 0, "Duplicate aliases:" + Environment.NewLine + string.Join(Environment.NewLine, duplicates));
    }

    [Fact]
    public void GrainTypeNamesAreBrandNeutral()
    {
        var offenders = ProductionArchitecture.Types
            .SelectMany(type => type.GetCustomAttributesData()
                .Where(data => data.AttributeType == typeof(GrainTypeAttribute) || data.AttributeType == typeof(DefaultGrainTypeAttribute))
                .Select(data => (Type: type, Name: data.ConstructorArguments.FirstOrDefault().Value as string)))
            .Where(entry => entry.Name is not null && HasBrand(entry.Name))
            .Select(entry => $"{entry.Type.FullName} -> {entry.Name}")
            .Order(StringComparer.Ordinal).ToArray();
        Assert.True(offenders.Length == 0, "Grain types carrying a brand token:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
