using IntoChat.OS.Google.Gmail.Testing;

[assembly: AssemblyFixture(typeof(IntoChat.OS.Modules.Google.Gmail.Tests.Integration.GmailHost))]

namespace IntoChat.OS.Modules.Google.Gmail.Tests.Integration;

// One brain shared by the assembly over one token/profile stub; each fact works in its own person's brain or connection.
public sealed class GmailHost : BrainHostFixture
{
    private GoogleStub? _stub;
    public GoogleStub Stub => _stub ?? throw new InvalidOperationException("The stub starts first.");

    protected override TestBrain Configure(TestBrain brain)
        => GmailBrain.Compose(brain, _stub ?? throw new InvalidOperationException("The stub starts first."));

    public override async ValueTask InitializeAsync()
    {
        _stub = await GoogleStub.StartAsync(CancellationToken.None);
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_stub is { } stub) { await stub.DisposeAsync(); }
    }
}
