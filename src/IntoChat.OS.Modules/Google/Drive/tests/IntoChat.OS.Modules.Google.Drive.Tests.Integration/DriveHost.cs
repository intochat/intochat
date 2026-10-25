[assembly: AssemblyFixture(typeof(IntoChat.OS.Modules.Google.Drive.Tests.Integration.DriveHost))]

namespace IntoChat.OS.Modules.Google.Drive.Tests.Integration;

// One brain with google-auth and google-drive shared by the assembly over one Drive stub.
public sealed class DriveHost : BrainHostFixture
{
    private DriveStub? _drive;
    internal DriveStub Drive => _drive ?? throw new InvalidOperationException("The stub starts first.");

    protected override TestBrain Configure(TestBrain brain) => DriveBrain.Compose(brain, Drive);

    public override async ValueTask InitializeAsync()
    {
        _drive = await DriveStub.StartAsync(CancellationToken.None);
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_drive is { } drive) { await drive.DisposeAsync(); }
    }
}
