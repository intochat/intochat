using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using IntoChat.Aspire.Candidates;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Tests.Integration.Components.Aspire;

// Integration verification of Plane 2 leased devcontainer slots (agent-dev-slots):
// verifies real OS TCP port binding, collision detection against live sockets, concurrent candidate
// container lifecycle with real HTTP /health checks, ephemeral $HOME cleanup, and warm slot reuse.
public sealed class AgentDevSlotsCandidateHostIntegrationFacts : IDisposable
{
    private readonly string _workdir;

    public AgentDevSlotsCandidateHostIntegrationFacts()
    {
        _workdir = Path.Combine(Path.GetTempPath(), "intochat-devslots-int-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workdir);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_workdir))
        {
            try
            {
                Directory.Delete(_workdir, recursive: true);
            }
            catch
            {
                // Best-effort temp test directory cleanup.
            }
        }
    }

    [Fact]
    public async Task DynamicPortAllocator_DetectsRealBoundTcpSocket_AndLeasesCollisionFreeRange()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var boundPort = ((IPEndPoint)occupied.LocalEndpoint).Port;

        // Start pool directly at the port currently bound by a real OS TCP listener.
        var basePort = Math.Clamp(boundPort, 1024, 60000);
        var options = new CandidateSlotPoolOptions
        {
            MaxSlots = 2,
            BasePort = basePort,
            PortStride = 10,
            EnableWarmSlotReuse = false,
        };
        await using var pool = new CandidateSlotLeasePool(options);

        await using var lease = await pool.AcquireAsync("a1b2c3d", _workdir, ct: Ct);

        Assert.False(lease.Ports.Contains(boundPort));
        Assert.False(lease.Ports.Contains(5080));
        Assert.False(lease.Ports.Contains(5081));
        Assert.False(lease.Ports.Contains(5190));

        // Prove we can bind a real OS TCP listener to the leased HTTP port without collision.
        var leasedPort = lease.Ports.HttpPort;
        using var candidateListener = new TcpListener(IPAddress.Loopback, leasedPort);
        candidateListener.Start();
        Assert.Equal(leasedPort, ((IPEndPoint)candidateListener.LocalEndpoint).Port);
        candidateListener.Stop();
    }

    [Fact]
    public async Task ParallelPoolsSharingSameBasePort_NeverAllocateOverlappingPorts()
    {
        var sharedBase = FindFreeBasePort();
        var options = new CandidateSlotPoolOptions
        {
            MaxSlots = 2,
            BasePort = sharedBase,
            PortStride = 10,
            EnableWarmSlotReuse = false,
        };

        await using var poolA = new CandidateSlotLeasePool(options);
        await using var poolB = new CandidateSlotLeasePool(options);

        var leases = await Task.WhenAll(
            poolA.AcquireAsync("aaa1111", _workdir, ct: Ct).AsTask(),
            poolA.AcquireAsync("aaa2222", _workdir, ct: Ct).AsTask(),
            poolB.AcquireAsync("bbb1111", _workdir, ct: Ct).AsTask(),
            poolB.AcquireAsync("bbb2222", _workdir, ct: Ct).AsTask());

        try
        {
            var allPorts = leases.SelectMany(l => l.Ports.AllPorts()).ToArray();
            Assert.Equal(allPorts.Length, allPorts.Distinct().Count());
        }
        finally
        {
            foreach (var lease in leases)
            {
                await lease.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ConcurrentCandidates_BindRealDynamicPorts_PassHealthProbes_AndCleanUpEphemeralHomes()
    {
        // Pick an ephemeral starting range dynamically to stay isolated from parallel CI test runners.
        var dynamicBase = FindFreeBasePort();
        var slotOptions = new CandidateSlotPoolOptions
        {
            MaxSlots = 3,
            BasePort = dynamicBase,
            PortStride = 10,
            EnableWarmSlotReuse = true,
        };

        var runner = new LiveSocketContainerRunner();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var config = new ConfigurationBuilder().Build();
        await using var pool = new CandidateSlotLeasePool(slotOptions);
        await using var host = new AgentDevSlotsCandidateHost(
            runner,
            config,
            pool,
            _ => Task.FromResult(_workdir),
            http: http,
            options: slotOptions,
            dependenciesReady: (_, _) => Task.CompletedTask);

        string[] shas = ["100aaaa", "200bbbb", "300cccc"];
        var runTasks = shas.Select(sha => host.Run(sha, Ct)).ToArray();
        var running = await Task.WhenAll(runTasks);

        try
        {
            var ports = running.Select(r => new Uri(r.Endpoints.Ui).Port).ToArray();
            Assert.Equal(3, ports.Distinct().Count());
            Assert.All(ports, p =>
            {
                Assert.NotEqual(5080, p);
                Assert.NotEqual(5081, p);
                Assert.NotEqual(5190, p);
            });

            var homes = new List<string>();
            foreach (var sha in shas)
            {
                Assert.True(pool.TryGetActiveLease(sha, out var lease));
                Assert.NotNull(lease);
                Assert.True(Directory.Exists(lease.EphemeralHome));
                homes.Add(lease.EphemeralHome);
            }

            Assert.Equal(3, homes.Distinct(StringComparer.Ordinal).Count());

            // All three candidates answer real HTTP /health concurrently over loopback TCP sockets.
            await Task.WhenAll(shas.Select(sha => host.WaitHealthy(sha, TimeSpan.FromSeconds(5), Ct)));

            // Remove all three candidates; verify ephemeral homes deleted and warm slots returned.
            await Task.WhenAll(shas.Select(sha => host.Remove(sha, Ct)));
            Assert.All(homes, home => Assert.False(Directory.Exists(home)));
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(3, pool.AvailableCount);
            Assert.Equal(3, pool.Snapshot().WarmCount);

            // Follow-up candidate immediately acquires a pre-warmed slot and passes real HTTP /health.
            var fourth = await host.Run("400dddd", Ct);
            Assert.Equal(1, host.WarmReuseHits);
            await host.WaitHealthy(fourth.Sha, TimeSpan.FromSeconds(5), Ct);
            await host.Remove(fourth.Sha, Ct);

            await host.DrainWarmSlotsAsync(Ct);
            Assert.Equal(0, pool.Snapshot().WarmCount);
        }
        finally
        {
            await runner.DisposeAsync();
        }
    }

    private static int FindFreeBasePort()
    {
        // Pick a starting base port in [20000, 30000), outside both Linux (32768-60999) and Windows (49152-65535)
        // OS ephemeral port ranges so concurrent tests' outbound sockets cannot collide in the bind window.
        return Random.Shared.Next(2000, 3000) * 10;
    }

    // Simulates Docker container lifecycle while binding real loopback TCP listeners for candidate HTTP /health ports.
    private sealed class LiveSocketContainerRunner : IProcessRunner, IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, ContainerEndpoint> _containers = new(StringComparer.Ordinal);

        private sealed class ContainerEndpoint(TcpListener listener, CancellationTokenSource cts, Task loop) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                await cts.CancelAsync().ConfigureAwait(false);
                listener.Stop();
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected on shutdown.
                }
                catch (SocketException)
                {
                    // Expected when listener stops.
                }

                cts.Dispose();
            }
        }

        public async Task<ProcessResult> RunAsync(
            string file,
            string[] args,
            string workingDirectory,
            IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct)
        {
            if (file == "docker" && args.Length > 0 && args[0] == "run")
            {
                var name = FlagValue(args, "--name");
                var portMapping = FlagValue(args, "-p");
                if (name is not null && args.Contains("--user") && portMapping is not null)
                {
                    var parts = portMapping.Split(':');
                    Assert.Equal("127.0.0.1", parts[0]);
                    var hostPort = int.Parse(parts[^2], System.Globalization.CultureInfo.InvariantCulture);
                    StartHealthEndpoint(name, hostPort);
                }

                return new ProcessResult(0, "container-id\n", "");
            }

            if (file == "docker" && args is ["inspect", "-f", "{{.State.Running}}", var inspectName])
            {
                var running = _containers.ContainsKey(inspectName) || !inspectName.Contains("-candidate-", StringComparison.Ordinal);
                return new ProcessResult(0, running ? "true\n" : "false\n", "");
            }

            if (file == "docker" && args.Length > 0 && args[0] == "rm")
            {
                foreach (var arg in args.Skip(1).Where(a => !a.StartsWith('-')))
                {
                    if (_containers.TryRemove(arg, out var endpoint))
                    {
                        await endpoint.DisposeAsync().ConfigureAwait(false);
                    }
                }

                return new ProcessResult(0, "", "");
            }

            return new ProcessResult(0, "", "");
        }

        public int Detach(string file, string[] args, string workingDirectory) => 1;

        public async ValueTask DisposeAsync()
        {
            foreach (var key in _containers.Keys.ToArray())
            {
                if (_containers.TryRemove(key, out var endpoint))
                {
                    await endpoint.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        private void StartHealthEndpoint(string containerName, int hostPort)
        {
            var listener = new TcpListener(IPAddress.Loopback, hostPort);
            if (!OperatingSystem.IsWindows())
            {
                // Mirror Docker/Kestrel SO_REUSEADDR on Unix so warm-slot reuse can rebind immediately
                // after a prior /health connection leaves a socket in TIME_WAIT.
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            }

            listener.Start();
            var cts = new CancellationTokenSource();
            var loop = Task.Run(async () =>
            {
                var responseBytes = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
                while (!cts.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                    await using var stream = client.GetStream();
                    var buffer = new byte[512];
                    _ = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
                    await stream.WriteAsync(responseBytes, cts.Token).ConfigureAwait(false);
                    await stream.FlushAsync(cts.Token).ConfigureAwait(false);
                }
            });
            _containers[containerName] = new ContainerEndpoint(listener, cts, loop);
        }

        private static string? FlagValue(string[] args, string flag)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == flag)
                {
                    return args[i + 1];
                }
            }

            return null;
        }
    }
}
