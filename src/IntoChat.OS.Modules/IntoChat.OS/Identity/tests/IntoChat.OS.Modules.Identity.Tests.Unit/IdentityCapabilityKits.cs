using System.Collections.Concurrent;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing.Conformance;
using DigitalBrain.Testing.Conformance.Identity;
using DigitalBrain.Testing.Conformance.Secrets;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Authority;

internal sealed class RuntimeCallerScope : CallerScope
{
    public override IDisposable As(CapabilityCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return RuntimeCallerContext.Stamp(CapabilityCallers.Of(caller.PrincipalId, caller.AccountId, caller.BrainId, caller.Kind, caller.AppId, caller.ConversationId));
    }
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => new Logger(_lines);

    public void Dispose() { }

    private sealed class Logger(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => lines.Enqueue($"{logLevel} {eventId.Name} {formatter(state, exception)} {exception}");
    }
}

public sealed class SecretsCapabilityConformance : SecretsConformance<SecretsCapability>
{
    protected override Task<SecretsSubject> Connect(CancellationToken ct)
    {
        var logs = new CapturingLoggerProvider();
        var vault = new InMemorySecretVault();
        var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var secrets = new SecretsCapability(vault, factory.CreateLogger<SecretsCapability>()) { Declarations = KitDeclarations.Identity };
        vault.Provide(KitDeclarations.NamedSecret.Name, KitDeclarations.NamedSecret.Value);
        return Task.FromResult(new SecretsSubject(secrets, new RuntimeCallerScope(), () => [.. logs.Lines, .. vault.Log]) { Named = KitDeclarations.NamedSecret });
    }
}

public sealed class IdentityCapabilityConformance : IdentityConformance<IdentityCapability>
{
    protected override Task<IdentitySubject> Connect(CancellationToken ct)
    {
        var directory = new InMemoryIdentityDirectory();
        var sessions = new SessionsCapability(directory, KitDeclarations.Identity);
        return Task.FromResult(new IdentitySubject(new IdentityCapability(directory), new GrantsCapability(directory), new RuntimeCallerScope(), directory.SignIn)
        {
            Sessions = sessions,
            Description = sessions,
            PrivilegedModule = KitDeclarations.PrivilegedModule,
        });
    }
}

internal static class KitDeclarations
{
    public const string PrivilegedModule = "kit-auth";

    public static KitNamedSecret NamedSecret { get; } = new(PrivilegedModule, "kit-issuer:SigningKey", $"v-{Guid.NewGuid():N}");

    public static ModuleDeclarations Identity { get; } = new([new ModuleManifest([], [], [], [], [Secret.Named("kit-issuer")])
    {
        Id = PrivilegedModule,
        Privileged = [new(Capability.Identity.V1, "Issue"), new(Capability.Identity.V1, "End"), new(Capability.Identity.V1, "Validate")],
    }]);
}
