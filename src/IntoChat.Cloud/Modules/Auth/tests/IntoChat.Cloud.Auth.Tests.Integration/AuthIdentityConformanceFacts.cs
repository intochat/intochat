using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Spi;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Conformance.Identity;
using DigitalBrain.Testing.Conformance.Secrets;
using IntoChat.Cloud.Auth.Services;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.Cloud.Auth.Tests;

// ROADMAP success criterion 1 (09-05): the Auth module passes the identity@1 kit as the provider of sessions and description.
// The session half is Auth's own port (AuthIdentityProvider over the sessions neurons, on TestOS) behind the runtime
// SessionsCapability, which enforces the manifest's privileged grants; accounts, brains and grants are the kit's in-memory
// directory behind the runtime capabilities, as the kernel's directory is in production.
public sealed class AuthIdentityConformanceFacts : IdentityConformance<AuthModule>, IAsyncLifetime
{
    private readonly List<TestOS> hosts = [];

    protected override Task<IdentitySubject> Connect(CancellationToken ct)
    {
        var host = new TestAuthHostSeam();
        var vault = new InMemorySecretVault();
        vault.Provide(AuthSecrets.HashingKey, AuthHost.AuthHashingKey);
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new AuthModuleOptions { Posture = DigitalBrain.Capabilities.Identity.IdentityPosture.Secured });
        services.AddKeyedSingleton<ISecrets>(Capability.Secrets.V1Id, (_, _) => new SecretsCapability(vault, null) { Declarations = AuthCapabilities.Declarations });
        services.AddSingleton<IAuthHostSeam>(host);
        services.AddSingleton<AuthSecrets>();
        services.AddSingleton<AuthHashing>();
        var provider = services.BuildServiceProvider();

        var os = TestOS.Local(options => options.Services = provider);
        hosts.Add(os);
        os.Register(typeof(AuthModule).Assembly);
        var calls = new AuthCalls(new TestOsProxies(os), host);
        var port = new AuthIdentityProvider(calls, provider.GetRequiredService<AuthHashing>(), provider.GetRequiredService<AuthModuleOptions>());

        var directory = new InMemoryIdentityDirectory();
        var sessions = new SessionsCapability(port, AuthCapabilities.Declarations);
        return Task.FromResult(new IdentitySubject(new IdentityCapability(directory), new GrantsCapability(directory), new RuntimeCallerScope(), directory.SignIn)
        {
            Sessions = sessions,
            Description = sessions,
            PrivilegedModule = AuthModule.Id,
        });
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var os in hosts) { await os.DisposeAsync(); }
    }

    private sealed class TestOsProxies(TestOS os) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class => os.Get<T>(id);
    }
}
