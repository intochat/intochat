using IntoChat.OS.Aspire.Server;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Orleans.Hosting;
using IntoChat.OS.Connections;

namespace IntoChat.OS.Framework.Tests.Integration.Licensing;

public sealed class LicenseGateFacts
{
    private const string Secret = "TOKEN-abc123";

    [Fact]
    public async Task AnUnlicensedModuleIsAbsentAndReportedDegradedOnHealthOnly()
    {
        var check = FakeLicenseCheck.Unlicensed("expired");
        await using var app = Build(check, requiredPaid: null);
        var inventory = app.Services.GetRequiredService<ModuleInventory>();
        var health = app.Services.GetRequiredService<HealthCheckService>();

        Assert.Equal(["free"], inventory.Modules.Select(module => module.Id.ToString()));
        Assert.DoesNotContain(typeof(PaidModule), inventory.Types);
        Assert.DoesNotContain(typeof(PaidModule).Assembly, inventory.ContractAssemblies());
        Assert.DoesNotContain(inventory.Types.SelectMany(ConnectorDefinition.DeclaredBy), connector => connector.Id == "paid");
        Assert.Equal([ModuleState.Active, ModuleState.Unlicensed], inventory.Statuses.OrderBy(s => s.Module.ToString()).Select(s => s.State).Order());
        var status = inventory.Statuses.Single(s => s.State == ModuleState.Unlicensed);
        Assert.Equal(("paid", "license: expired"), (status.Module.ToString(), status.Reason));
        Assert.Equal(("test-paid", "paid"), (check.Calls.Single().Product, check.Calls.Single().Module.ToString()));

        var report = await health.CheckHealthAsync(HealthProbes.Health, TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Degraded, report.Status);
        Assert.Equal(HealthStatus.Degraded, report.Entries["module-paid"].Status);
        Assert.Equal("license: expired", report.Entries["module-paid"].Description);
    }

    [Fact]
    public async Task AnUnlicensedModuleLeavesReadyAndAliveHealthy()
    {
        await using var app = Build(FakeLicenseCheck.Unlicensed("missing"), requiredPaid: null);
        var health = app.Services.GetRequiredService<HealthCheckService>();

        var ready = await health.CheckHealthAsync(HealthProbes.Ready, TestContext.Current.CancellationToken);
        var alive = await health.CheckHealthAsync(HealthProbes.Alive, TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, ready.Status);
        Assert.Equal(HealthStatus.Healthy, alive.Status);
        Assert.DoesNotContain("module-paid", ready.Entries.Keys);
        Assert.DoesNotContain("module-paid", alive.Entries.Keys);
    }

    [Fact]
    public void ARequiredUnlicensedModuleFailsStartupWithoutLeakingTheToken()
    {
        var check = new FakeLicenseCheck(_ => new LicenseResult(false, Secret, null, Secret));

        var error = Assert.Throws<InvalidOperationException>(() => Build(check, requiredPaid: true));

        Assert.Contains("'paid'", error.Message);
        Assert.Contains("license: invalid", error.Message);
        Assert.DoesNotContain(Secret, error.ToString());
    }

    [Fact]
    public async Task ALicensedModuleIsActiveAndHealthy()
    {
        await using var app = Build(FakeLicenseCheck.Licensed, requiredPaid: true);
        var inventory = app.Services.GetRequiredService<ModuleInventory>();

        Assert.Equal(["free", "paid"], inventory.Modules.Select(module => module.Id.ToString()).Order());
        Assert.All(inventory.Statuses, status => Assert.Equal(ModuleState.Active, status.State));
        Assert.Single(inventory.Types.SelectMany(ConnectorDefinition.DeclaredBy), connector => connector.Id == "paid");
        var report = await app.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(HealthProbes.Health, TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Healthy, report.Status);
    }

    [Fact]
    public async Task WithoutALicenseCheckAPaidModuleIsUnlicensed()
    {
        await using var app = Build(null, requiredPaid: null);
        var inventory = app.Services.GetRequiredService<ModuleInventory>();

        Assert.Equal(["free"], inventory.Modules.Select(module => module.Id.ToString()));
        Assert.Equal("license: missing", inventory.Statuses.Single(s => s.State == ModuleState.Unlicensed).Reason);
    }

    [Fact]
    public async Task ADisabledModuleIsReportedDisabledWithoutBeingChecked()
    {
        var check = FakeLicenseCheck.Licensed;
        var builder = NewBuilder();
        builder.Configuration[IntoChatOSConfiguration.ModuleSection("paid") + ":Enabled"] = "false";
        builder.AddIntoChatOSServer(server => server.UseLicenseCheck(check).AddModule<FreeModule>().AddModule<PaidModule>());
        await using var app = builder.Build();

        var inventory = app.Services.GetRequiredService<ModuleInventory>();
        Assert.Equal(ModuleState.Disabled, inventory.Statuses.Single(s => s.Module.ToString() == "paid").State);
        Assert.Empty(check.Calls);
    }

    // Neither Development (whose service-provider validation these minimal hosts do not satisfy) nor Production: a facts run
    // must not depend on the ASPNETCORE_ENVIRONMENT of the machine it runs on.
    private static WebApplicationBuilder NewBuilder() => WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });

    [Fact]
    public void ALicensedCheckIsNotSharedBetweenFacts()
    {
        var first = FakeLicenseCheck.Licensed;
        first.Check(new ModuleId("paid"), new LicenseContext(new ConfigurationBuilder().Build(), "test-paid"));

        Assert.Empty(FakeLicenseCheck.Licensed.Calls);
    }

    private static WebApplication Build(ILicenseCheck? check, bool? requiredPaid)
    {
        var builder = NewBuilder();
        if (requiredPaid is { } required) { builder.Configuration[IntoChatOSConfiguration.ModuleSection("paid") + ":Required"] = required.ToString(); }
        builder.AddIntoChatOSServer(server =>
        {
            if (check is not null) { server.UseLicenseCheck(check); }
            server.AddModule<FreeModule>().AddModule<PaidModule>();
        });
        return builder.Build();
    }

    private sealed class FreeModule() : Module(new ModuleId("free"));

    private sealed class PaidModule() : Module(new ModuleId("paid"))
    {
        public static ConnectorDefinition Connector => new("paid", "Paid", ["api-key"], []);
        public override string? LicenseProduct => "test-paid";
        public override System.Reflection.Assembly? Contracts => typeof(PaidModule).Assembly;
    }
}
