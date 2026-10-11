using DigitalBrain.Flutter;

namespace DigitalBrain.Modules.Flutter.Tests;

// One brain with the Flutter module shared by the HTTP facts for speed; each fact registers its own person,
// so routes, neuron keys and signals land in that person's brain.
public sealed class FlutterHostFixture : IAsyncLifetime
{
    private ITestBrain? _brain;

    public ITestBrain Brain => _brain ?? throw new InvalidOperationException("The Flutter host has not started yet.");

    public async ValueTask InitializeAsync()
        => _brain = await TestBrain.Create()
            .WithModule<FlutterModule, FlutterModuleOptions>(flutter => flutter.BackendOnly())
            .StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_brain is not null) { await _brain.DisposeAsync(); }
    }

    public Task<DigitalBrain.Testing.Person> PersonAsync(CancellationToken ct) => Brain.PersonAsync("p" + Guid.NewGuid().ToString("N")[..12], ct);
}

[CollectionDefinition(Name)]
public sealed class FlutterHostCollection : ICollectionFixture<FlutterHostFixture>
{
    public const string Name = "flutter-http-host";
}
