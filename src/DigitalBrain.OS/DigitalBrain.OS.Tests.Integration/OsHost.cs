using DigitalBrain.Testing.Model;

[assembly: AssemblyFixture(typeof(DigitalBrain.OS.Tests.Integration.OsHost))]

namespace DigitalBrain.OS.Tests.Integration;

// The one in-process OS this assembly shares: its fake model and Google token endpoint live as long as the host.
public sealed class OsHost : BrainHostFixture
{
    private FakeModelServer? _model;
    private TokenEndpointStub? _tokens;

    public FakeModelServer Model => _model ?? throw new InvalidOperationException("The host starts in InitializeAsync.");
    public TokenEndpointStub Tokens => _tokens ?? throw new InvalidOperationException("The host starts in InitializeAsync.");

    protected override TestBrain Configure(TestBrain brain) => brain.WithOperatingSystem(Model, Tokens);

    public override async ValueTask InitializeAsync()
    {
        _model = await FakeModelServer.StartAsync(CancellationToken.None);
        _tokens = await TokenEndpointStub.StartAsync(CancellationToken.None);
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_tokens is not null) { await _tokens.DisposeAsync(); }
        if (_model is not null) { await _model.DisposeAsync(); }
    }
}
