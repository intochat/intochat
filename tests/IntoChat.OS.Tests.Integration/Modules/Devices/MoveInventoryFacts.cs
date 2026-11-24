using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Kernel.Install;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Runtime;
using IntoChat.OS.Tests.Integration.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Integration.Modules.Devices;

// The cloud's record move needs the OS install's brains, principals and enrolled machines (cloud report 13). A real host writes its
// store and stops; the operator verbs then open that store offline with the host's own key custody: the inventory lists what the
// directory and the enrollments hold, and the move copies an enrollment record out of it.
[Trait("Category", "AccessFindings")]
public sealed class MoveInventoryFacts
{
    private const string MasterKey = "move-inventory-facts-master-key-0123456789";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IConfiguration Keys = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["IntoChatOS:MasterKey"] = MasterKey,
    }).Build();

    private static async Task<(int Code, string Output, string Error)> Inventory(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await MoveInventoryCommand.Run(args, Keys, output, error, Ct);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task TheInventoryListsTheDirectoryAndTheEnrolledMachines_OfAStoppedHostsStore_AndTheMoveOpensItToo()
    {
        using var root = new TempDirectory("move-inventory-");
        var home = Path.Combine(root.Path, "home");
        var principal = "cloud-ada-" + Guid.NewGuid().ToString("N")[..8];
        string brain;
        string revoked = "m-revoked";
        await using (var host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(Ct, builder =>
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [IntoChatOSNames.HomeKey] = home,
                ["IntoChatOS:MasterKey"] = MasterKey,
            })))
        {
            await host.Services.GetRequiredService<IIdentityClient>().RegisterPrincipal(principal, "Ada");
            brain = host.Scope;
            var edge = host.Services.GetRequiredService<EdgeAuthority>();
            var enrollment = host.Services.GetRequiredService<INeuronProxies>().Get<IDeviceEnrollment>(IDeviceEnrollment.Key(brain));
            var redeemer = host.Services.GetRequiredService<IDeviceEnrollmentRedeemer>();
            foreach (var machine in new[] { "m-live", revoked })
            {
                Code code;
                using (edge.Enter(edge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, new BrainId(brain)))) { code = await enrollment.Mint(); }
                Assert.Equal(EnrollmentResult.Accepted, (await redeemer.Redeem(code.Value, machine, "thumb-" + machine, machine)).Result);
            }
            using (edge.Enter(edge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, new BrainId(brain)))) { await enrollment.Revoke(revoked); }
        }

        var lists = Path.Combine(root.Path, "lists");
        var done = await Inventory("move-inventory", "--home", home, "--out", lists);

        Assert.True(done.Code == 0, done.Error);
        var brains = await File.ReadAllLinesAsync(Path.Combine(lists, "brains.txt"), Ct);
        var principals = await File.ReadAllLinesAsync(Path.Combine(lists, "principals.txt"), Ct);
        var machines = await File.ReadAllLinesAsync(Path.Combine(lists, "machines.txt"), Ct);
        Assert.Contains("system", brains);
        Assert.Contains(brain, brains);
        Assert.Contains(principal, principals);
        Assert.Contains(AccountSession.DefaultLogin, principals);
        Assert.Equal([brain + " m-live"], machines);

        // The move opens the same stopped store with the same custody: the enrollment record is backed up and copied out.
        var plan = new DigitalBrain.Runtime.Orleans.BrainMovePlan(home, Path.Combine(root.Path, "cloud-home"), Path.Combine(root.Path, "backup"), brains,
            [new DigitalBrain.Runtime.Orleans.BrainRecordRule(Neuron: "devices.enrollment")]);
        var planFile = Path.Combine(root.Path, "plan.json");
        await File.WriteAllTextAsync(planFile, System.Text.Json.JsonSerializer.Serialize(plan, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), Ct);
        foreach (var phase in new[] { "backup", "copy" })
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.True(await RecordMoveCommand.Run(["move-records", planFile, phase], Keys, output, error, Ct) == 0, error.ToString());
            if (phase == "copy") { Assert.Contains($"copy {brain}: selected 1, copied 1", output.ToString(), StringComparison.Ordinal); }
        }
    }

    [Fact]
    public async Task UsageAMissingKeyOrAStoreWithoutADirectoryIsRefused()
    {
        using var root = new TempDirectory("move-inventory-empty-");

        Assert.Equal(64, (await Inventory("move-inventory", "--home", root.Path)).Code);
        Assert.Equal(1, (await Inventory("move-inventory", "--home", root.Path, "--out", Path.Combine(root.Path, "out"))).Code);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await MoveInventoryCommand.Run(["move-inventory", "--home", root.Path, "--out", root.Path], new ConfigurationBuilder().Build(), output, error, Ct));
        Assert.Contains("MasterKey is not set", error.ToString(), StringComparison.Ordinal);
    }
}
