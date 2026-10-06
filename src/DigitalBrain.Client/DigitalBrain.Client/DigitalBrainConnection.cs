using System.Runtime.InteropServices;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Client.Edge.V1;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Client;

public sealed class DigitalBrainConnection : IDigitalBrain, IAsyncDisposable
{
    private readonly IConfigurationRoot _configuration;
    private readonly CancellationTokenSource _stopping = new();
    private readonly PosixSignalRegistration _termination;
    private bool _disposed;
    private readonly HttpClient _http;
    private readonly ScriptEdgeClient _edge;

    internal DigitalBrainConnection(IConfigurationRoot configuration, HttpClient http)
    {
        _configuration = configuration;
        _termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        { context.Cancel = true; _stopping.Cancel(); });
        Console.CancelKeyPress += Cancel;
        _http = http;
        _edge = new ScriptEdgeClient(http);
    }

    public CancellationToken Stopping => _stopping.Token;

    private void Cancel(object? sender, ConsoleCancelEventArgs args) { args.Cancel = true; _stopping.Cancel(); }

    // Environment variables spell configuration sections with "__", so "Account__twitter" arrives as "Account:twitter".
    public string? Setting(string name) => Configuration["CSharpFile:Settings:" + name.Replace("__", ":", StringComparison.Ordinal)];

    // A run started by an armed file's trigger carries that signal; other runs have none.
    public T Trigger<T>() where T : Signal
        => Configuration["CSharpFile:Trigger"] is { Length: > 0 } json
            ? JsonSerializer.Deserialize<T>(json, ScriptEdgeProtocol.Json) ?? throw new InvalidOperationException("The trigger signal is empty.")
            : throw new InvalidOperationException("This run was not started by a trigger.");

    internal static DigitalBrainConnection ForTests(HttpClient http) => new(new ConfigurationBuilder().Build(), http);

    public T Get<T>(NeuronId id) where T : INeuron => NeuronProxy.Create<T>(_edge, id.Name);

    // The edge tells a run which brain it serves; a run without one cannot reach its kernel.
    public IKernel Self => Configuration["CSharpFile:Brain"] is { Length: > 0 } brain
        ? NeuronProxy.Create<IKernel>(_edge, brain)
        : throw new InvalidOperationException("This run was not told its brain.");

    public IAsyncEnumerable<T> On<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal
    {
        if (source is not NeuronProxy neuron) { throw new ArgumentException("Watch a neuron obtained from this connection's Get.", nameof(source)); }
        return new WatchStream<T>(async ct =>
        {
            var subscription = await EdgeSignalSubscription<T>.OpenAsync(_edge, neuron, ct).ConfigureAwait(false);
            return (subscription.ReadAllAsync(ct), subscription);
        });
    }

    private IConfiguration Configuration => _configuration;

    public ValueTask DisposeAsync()
    {
        if (_disposed) { return ValueTask.CompletedTask; }
        _disposed = true;
        Console.CancelKeyPress -= Cancel;
        _termination.Dispose();
        _stopping.Cancel();
        _http.Dispose();
        (_configuration as IDisposable)?.Dispose();
        _stopping.Dispose();
        return ValueTask.CompletedTask;
    }
}
