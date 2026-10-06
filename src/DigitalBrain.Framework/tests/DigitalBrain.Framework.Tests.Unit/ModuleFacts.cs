namespace DigitalBrain.Framework.Tests.Unit;

public sealed class ModuleFacts
{
    private sealed class ExampleOptions : DigitalBrain.IModuleOptions { public void Validate() { } }
    private sealed class ExampleModule() : DigitalBrain.Framework.Module<ExampleOptions>(new("example"))
    { public override void Configure(Orleans.Hosting.ISiloBuilder silo) { } }

    [Fact]
    public void A_module_knows_its_id_from_birth()
        => Assert.Equal("example", new ExampleModule().Id.ToString());
}
