using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Framework.Tests.Integration;

public sealed class ModuleFacts
{
    private sealed class ExampleOptions : IModuleOptions { public void Validate() { } }
    private sealed class ExampleModule() : Module<ExampleOptions>(new("example"))
    { public override void Configure(Orleans.Hosting.ISiloBuilder silo) { } }

    private sealed class DeclaringModule() : Module(new("declaring"))
    {
        public override System.Reflection.Assembly? Contracts => typeof(ModuleFacts).Assembly;
        public override void Configure(Orleans.Hosting.ISiloBuilder silo) { }
    }

    [Fact]
    public void A_module_knows_its_id_from_birth()
        => Assert.Equal("example", new ExampleModule().Id.ToString());

    [Fact]
    public void A_module_exposes_no_contracts_unless_it_declares_them()
        => Assert.DoesNotContain(typeof(ModuleFacts).Assembly, new ModuleInventory([new ExampleModule()]).ContractAssemblies());

    [Fact]
    public void A_declared_contract_assembly_is_exposed_and_names_its_module()
    {
        var module = new DeclaringModule();
        var inventory = new ModuleInventory([module]);

        Assert.Contains(typeof(ModuleFacts).Assembly, inventory.ContractAssemblies());
        Assert.Same(module, inventory.Declaring(typeof(ModuleFacts).Assembly));
    }
}
