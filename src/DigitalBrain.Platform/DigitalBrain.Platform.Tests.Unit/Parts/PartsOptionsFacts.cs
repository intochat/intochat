using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Sequencer;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

public sealed class PartsOptionsFacts
{
    [Fact]
    public void OwnMachinesHoldLongLeasesAndAcknowledgeOnTheJournal()
    {
        var own = new PartsOptions { Role = MachineRole.Own };
        var cloud = new PartsOptions { Role = MachineRole.Cloud };
        Assert.True(own.Lease > TimeSpan.FromDays(1) && own.AcknowledgeOnJournal);
        Assert.True(cloud.Lease < TimeSpan.FromMinutes(5) && !cloud.AcknowledgeOnJournal);
    }

    [Fact(Timeout = 60_000)]
    public async Task EverySiloHasAMachineIdentityAndRollbackMemory()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        Assert.NotEmpty(brain.SiloServices.GetRequiredService<MachineIdentity>().MachineId);
        Assert.NotNull(brain.SiloServices.GetRequiredService<SeenVersions>());
    }
}
