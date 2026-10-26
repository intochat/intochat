using System.Collections.Concurrent;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing.Conformance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Kernel.Tests.Integration.Capabilities;

// One in-process kernel for the secrets@1 and identity@1 kits (05-12): the capabilities resolve through the runtime broker over
// the kernel's Authority ports, and every log line the host writes is captured so LogsNeverContainValue has teeth.
public sealed class CapabilityHost : IAsyncLifetime
{
    private ITestBrain? _brain;

    public CapturingLoggerProvider Logs { get; } = new();

    public ITestBrain Brain => _brain ?? throw new InvalidOperationException("The host starts in InitializeAsync.");

    public ICapabilityBroker Broker => Brain.Services.GetRequiredService<ICapabilityBroker>();

    // The broker resolves single-connection capabilities per brain; the kits stamp their own callers.
    public static DigitalBrain.BrainId KitBrain { get; } = new("kit");

    public async ValueTask InitializeAsync()
        => _brain = await TestBrain.Create().ConfigureServices(services => services.AddSingleton<ILoggerProvider>(Logs)).StartAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (_brain is not null) { await _brain.DisposeAsync().ConfigureAwait(false); }
    }
}

// The kit caller as the runtime sees it: stamped on the call context the capabilities read (RuntimeCallerContext).
public sealed class RuntimeCallerScope : CallerScope
{
    public override IDisposable As(KitCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return RuntimeCallerContext.Stamp(new CapabilityCaller(caller.PrincipalId, caller.AccountId, caller.BrainId, caller.AppId, caller.ConversationId));
    }
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose() { }

    private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lines.Enqueue($"{logLevel} {category} {formatter(state, exception)} {exception}");
        }
    }
}
