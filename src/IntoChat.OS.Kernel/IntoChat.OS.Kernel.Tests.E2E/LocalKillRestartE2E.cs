using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.AppHost;
using IntoChat.OS.Client.Orleans;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Modules.Time;
using IntoChat.OS.Modules.Time.Reminders;
using System.Runtime.CompilerServices;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Kernel.Tests.E2E;

// D-25 / TOPO-02, true-kill tier: the built Kernel host runs as a child process on Topology=local (the digitalbrain
// LocalProfile: file grain stores and the file reminder table under <Home>/state). A brain and a ReminderNeuron
// reminder are created, the process tree is killed without a graceful shutdown, the host is launched again over the
// same Home and ports, and the brain is still listed and the reminder ticks again. Timers would die with the process.
// Since 06-03 the reminder is the plain ReminderNeuron on the runtime's host grain neuron.host/{brain}/reminder/{key}, reached
// from the cluster client through its INeuronHost. A tick is a turn on that grain, so after the restart, with no call made,
// the host grain becoming active is the evidence that the persisted registration fired.
public sealed class LocalKillRestartE2E
{
    private const string ReminderKey = "kill-restart";
    private const string Principal = "kill-restart-owner";
    private static readonly TimeSpan Healthy = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan TickBound = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact(Timeout = 600_000)]
    public async Task ABrainAndItsReminderSurviveAProcessKill()
    {
        var ct = TestContext.Current.CancellationToken;
        var transcript = new List<string>();
        void Note(string line)
        {
            var stamped = $"{DateTimeOffset.UtcNow:O} {line}";
            transcript.Add(stamped);
            TestContext.Current.SendDiagnosticMessage(stamped);
        }
        // The local run's module defaults (what the AppHost projects for Topology=local), with the time module on.
        var settings = RunProfile.ModuleDefaults(IntoChatOSNames.TopologyLocal).ToDictionary(setting => setting.Key, setting => setting.Value!);
        settings[IntoChatOSConfiguration.ModuleSection("time") + ":Enabled"] = "true";
        // Test only, in the child's environment: the production reminder minimum is unchanged.
        settings[TimeModule.MinimumReminderPeriodKey] = "00:00:01";
        await using var host = new ChildProcessHost(KernelAssembly(), settings);
        try
        {
            await host.StartAsync(Healthy, ct);
            Note($"launch 1 healthy pid={host.ProcessId} topology=local home={host.Home.Path}");
            using var origin = new HttpClient { BaseAddress = host.BaseAddress };
            using var person = await Person.RegisterAsync(origin, Principal, ct);
            using (var created = await person.Http.PostAsync("/identity/brains", content: null, ct)) { created.EnsureSuccessStatusCode(); }
            var brainsBefore = await BrainsAsync(person.Http, ct);
            Note($"brains before kill: {string.Join(", ", brainsBefore)}");
            Assert.True(brainsBefore.Count >= 2);

            var id = new global::DigitalBrain.NeuronId(new global::DigitalBrain.BrainId(brainsBefore.Order(StringComparer.Ordinal).First()), "reminder", ReminderKey);
            var hostGrain = GrainId.Create("neuron.host", id.ToString());
            await using (var client = await ConnectAsync(host.GatewayPort, ct))
            {
                await NeuronProxy.Create<IReminder>(id, global::IntoChat.OS.Kernel.Composition.KernelTestHooks.HostSink(client.Services)).Start(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
                await ActiveAsync(client.Services, hostGrain, ct);
            }
            Note($"reminder started on {hostGrain}");

            var pid = host.ProcessId;
            await host.KillAsync();
            Note($"Kill(entireProcessTree: true) pid={pid}");

            await host.StartAsync(Healthy, ct);
            Note($"launch 2 healthy pid={host.ProcessId} same home and ports");
            using var login = new HttpClient(new SocketsHttpHandler { UseCookies = true, CookieContainer = new CookieContainer() }) { BaseAddress = host.BaseAddress };
            using (var signedIn = await login.PostAsJsonAsync("/identity/login", new { principalId = Principal, password = Principal + "-password-123" }, ct))
            { signedIn.EnsureSuccessStatusCode(); }
            var brainsAfter = await BrainsAsync(login, ct);
            Note($"brains after restart: {string.Join(", ", brainsAfter)}");
            Assert.Equal(brainsBefore.Order(), brainsAfter.Order());

            await using (var client = await ConnectAsync(host.GatewayPort, ct))
            {
                // No call here: only the registration persisted in the file reminder table can activate the host grain.
                await ActiveAsync(client.Services, hostGrain, ct);
                Note($"reminder fired after restart on {hostGrain}");
                await NeuronProxy.Create<IReminder>(id, global::IntoChat.OS.Kernel.Composition.KernelTestHooks.HostSink(client.Services)).Stop();
            }
        }
        finally
        {
            var output = Path.Combine(AppContext.BaseDirectory, "local-kill-restart");
            Directory.CreateDirectory(output);
            await File.WriteAllLinesAsync(Path.Combine(output, "transcript.log"), transcript, CancellationToken.None);
            if (File.Exists(host.LogPath)) { File.Copy(host.LogPath, Path.Combine(output, "host.log"), overwrite: true); }
        }
    }

    // The Kernel host's own build output sits beside this project's: <bin>/IntoChat.OS.Kernel/<configuration>.
    private static string KernelAssembly()
    {
        var own = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return Path.Combine(own.Parent!.Parent!.FullName, "IntoChat.OS.Kernel", own.Name, "IntoChat.OS.Kernel.dll");
    }

    private static async Task<List<string>> BrainsAsync(HttpClient http, CancellationToken ct)
    {
        var brains = await http.GetFromJsonAsync<JsonElement>("/identity/brains", Json, ct);
        return [.. brains.EnumerateArray().Select(brain => brain.GetProperty("id").GetString()!)];
    }

    // Waits until the grain has an activation in the cluster.
    private static async Task ActiveAsync(IServiceProvider client, GrainId grain, CancellationToken ct)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(TickBound);
        while (!await global::IntoChat.OS.Kernel.Composition.KernelTestHooks.IsActive(client, grain, bound.Token)) { await Task.Delay(250, bound.Token); }
    }

    private static async Task<ClientHost> ConnectAsync(int gatewayPort, CancellationToken ct)
    {
        var host = Host.CreateDefaultBuilder().ConfigureLogging(log => log.SetMinimumLevel(LogLevel.Warning))
            .UseOrleansClient(client =>
            {
                client.UseStaticClustering(new IPEndPoint(IPAddress.Loopback, gatewayPort));
                client.AddIntoChatOS();
            }).Build();
        try { await host.StartAsync(ct); }
        catch { host.Dispose(); throw; }
        return new ClientHost(host);
    }

    private sealed class ClientHost(IHost host) : IAsyncDisposable
    {
        public IServiceProvider Services => host.Services;

        public async ValueTask DisposeAsync()
        {
            await ((IAsyncDisposable)host.Services.GetRequiredService<IIntoChatOSBrains>()).DisposeAsync();
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }
    }
}
