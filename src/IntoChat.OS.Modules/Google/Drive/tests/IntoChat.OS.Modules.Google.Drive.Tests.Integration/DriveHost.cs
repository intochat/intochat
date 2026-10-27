using IntoChat.OS.Connections.Platform.Connections;
[assembly: AssemblyFixture(typeof(IntoChat.OS.Modules.Google.Drive.Tests.Integration.DriveHost))]

namespace IntoChat.OS.Modules.Google.Drive.Tests.Integration;

// One brain with google-auth and google-drive shared by the assembly over one Drive stub, plus the storage@1 kit's own brain
// over a second stub (05-09): the kit seeds folders under My Drive, which the legacy facts list exactly.
public sealed class DriveHost : BrainHostFixture
{
    private DriveStub? _drive;
    private DriveStub? _kitDrive;
    private ITestBrain? _kitBrain;
    internal DriveStub Drive => _drive ?? throw new InvalidOperationException("The stub starts first.");
    internal DriveStub KitDrive => _kitDrive ?? throw new InvalidOperationException("The stub starts first.");
    public ITestBrain KitBrain => _kitBrain ?? throw new InvalidOperationException("The kit brain starts in InitializeAsync.");

    protected override TestBrain Configure(TestBrain brain) => DriveBrain.Compose(brain, Drive);

    public override async ValueTask InitializeAsync()
    {
        _drive = await DriveStub.StartAsync(CancellationToken.None);
        _kitDrive = await DriveStub.StartAsync(CancellationToken.None);
        await base.InitializeAsync();
        _kitBrain = await DriveBrain.StartAsync(_kitDrive, CancellationToken.None);
    }

    // A fresh person's connected Drive on the kit brain, as the runtime broker resolves it, called as that person.
    public async Task<DigitalBrain.Capabilities.Storage.IStorage> ConnectKitAsync(CancellationToken ct)
    {
        var person = await KitBrain.PersonAsync($"kit-{Guid.NewGuid():N}", ct);
        var key = IntoChat.OS.Connections.Platform.Connections.ConnectionKeys.For(person.BrainId, DriveModule.Connector.Id, "primary");
        using (person.AsCaller())
        {
            await KitBrain.Plain().GetConnectionInbound(key).AcceptGrant(new("google-oauth", "drive-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1), DriveScopes.Read, null, "{}"));
            var broker = global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<DigitalBrain.Runtime.Orleans.Capabilities.CapabilityBroker>(KitBrain.Services);
            var connection = Assert.Single(await broker.Storages(new DigitalBrain.BrainId(person.BrainId), ct), item => item.ConnectionKey == key);
            return new AsOwner(connection.Capability, person);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_kitBrain is { } kit) { await kit.DisposeAsync(); }
        await base.DisposeAsync();
        if (_kitDrive is { } kitDrive) { await kitDrive.DisposeAsync(); }
        if (_drive is { } drive) { await drive.DisposeAsync(); }
    }
}
