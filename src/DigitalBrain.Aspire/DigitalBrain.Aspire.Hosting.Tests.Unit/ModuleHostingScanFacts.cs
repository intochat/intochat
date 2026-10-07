using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Aspire.Hosting.Tests.Unit;

public sealed class ModuleHostingScanFacts
{
    [Fact]
    public void An_adapter_referenced_by_the_root_is_found_under_its_module()
    {
        var adapters = ModuleHostingScan.Resolve(typeof(DigitalBrain.Postgres.Aspire.Hosting.PostgresModuleHosting).Assembly);
        Assert.IsType<DigitalBrain.Postgres.Aspire.Hosting.PostgresModuleHosting>(
            adapters[new ModuleId("postgres")]);
    }

    [Fact]
    public void A_module_with_no_referenced_adapter_is_simply_unclaimed()
    {
        var adapters = ModuleHostingScan.Resolve(typeof(ModuleHostingScanFacts).Assembly);
        Assert.False(adapters.ContainsKey(new ModuleId("no-such-module")));
    }
}
