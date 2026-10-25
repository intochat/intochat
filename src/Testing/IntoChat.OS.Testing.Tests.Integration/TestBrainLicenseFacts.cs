using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Orleans.Hosting;

namespace IntoChat.OS.Testing.Tests.Integration;

public sealed class TestBrainLicenseFacts
{
    [Fact]
    public async Task WithLicenseCheck_Valid_ModuleRegistered()
    {
        var ct = TestContext.Current.CancellationToken;
        var check = new FixedCheck(new LicenseResult(true, "holder", null, null));
        await using var brain = await TestBrain.Create().WithModule<LicensedProbeModule>().WithLicenseCheck(check).StartAsync(ct);

        var inventory = brain.Services.GetRequiredService<ModuleInventory>();
        Assert.Contains("licensed-probe", inventory.Modules.Select(module => module.Id.ToString()));
        Assert.Equal(ModuleState.Active, inventory.Statuses.Single(status => status.Module.ToString() == "licensed-probe").State);
        Assert.Equal("probe", Assert.Single(check.Products));
    }

    [Fact]
    public async Task WithLicenseCheck_Invalid_ModuleAbsentWithUnlicensedStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        var check = new FixedCheck(new LicenseResult(false, null, null, "expired"));
        await using var brain = await TestBrain.Create().WithModule<LicensedProbeModule>().WithLicenseCheck(check).StartAsync(ct);

        var inventory = brain.Services.GetRequiredService<ModuleInventory>();
        Assert.DoesNotContain("licensed-probe", inventory.Modules.Select(module => module.Id.ToString()));
        var status = inventory.Statuses.Single(status => status.Module.ToString() == "licensed-probe");
        Assert.Equal(ModuleState.Unlicensed, status.State);
        Assert.Equal("license: expired", status.Reason);
        var report = await brain.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(_ => true, ct);
        Assert.Equal(HealthStatus.Degraded, report.Entries["module-licensed-probe"].Status);
    }

    [Fact]
    public async Task NoLicenseCheck_LicensedModule_AbsentAsMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<LicensedProbeModule>().StartAsync(ct);

        var inventory = brain.Services.GetRequiredService<ModuleInventory>();
        Assert.DoesNotContain("licensed-probe", inventory.Modules.Select(module => module.Id.ToString()));
        Assert.Equal("license: missing", inventory.Statuses.Single(status => status.State == ModuleState.Unlicensed).Reason);
    }

    private sealed class FixedCheck(LicenseResult result) : ILicenseCheck
    {
        public List<string> Products { get; } = [];

        public LicenseResult Check(ModuleId module, LicenseContext context)
        {
            Products.Add(context.Product);
            return result;
        }
    }

    public sealed class LicensedProbeModule() : Module(new ModuleId("licensed-probe"))
    {
        public override string? LicenseProduct => "probe";
        public override void Configure(ISiloBuilder silo) { }
    }
}
