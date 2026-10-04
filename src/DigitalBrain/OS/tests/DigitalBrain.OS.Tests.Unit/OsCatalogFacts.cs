using DigitalBrain.Contracts;
using DigitalBrain.OS;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class OsCatalogFacts
{
    [Fact]
    public void EveryCatalogModuleDeclaresItsIdOnTheType()
    {
        Assert.NotEmpty(OsCatalog.Modules);
        foreach (var module in OsCatalog.Modules)
        {
            Assert.True(module.IsDefined(typeof(ModuleIdAttribute), inherit: false),
                $"{module.FullName} carries no [ModuleId].");
        }
    }

    [Fact]
    public void CatalogIdsAreUnique()
    {
        var ids = OsCatalog.Modules.Select(ModuleIdentity.Get).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }
}
