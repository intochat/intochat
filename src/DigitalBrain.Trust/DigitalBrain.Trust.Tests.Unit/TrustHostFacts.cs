using DigitalBrain.Framework;
using DigitalBrain.Platform.Sequencer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DigitalBrain.Trust.Tests.Unit;

// The trust host as it runs: its own silo, the trust grains, platform storage, and no module, kernel or neuron.
// Without the orleans integration it keeps its state in memory on localhost, as a developer's trust host does.
public sealed class TrustHostFacts
{
    [Fact(Timeout = 60_000)]
    public async Task TheTrustHostStartsWithNoModules()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{Platform.Trust.TrustOptions.Key}:{nameof(Platform.Trust.TrustOptions.SiloPort)}"] = Free().ToString(System.Globalization.CultureInfo.InvariantCulture),
            [$"{Platform.Trust.TrustOptions.Key}:{nameof(Platform.Trust.TrustOptions.IdentityPath)}"] = Path.Combine(Path.GetTempPath(), $"trust-{Guid.NewGuid():N}", "identity.key"),
            [$"{Platform.Trust.TrustOptions.Key}:{nameof(Platform.Trust.TrustOptions.AdvertisedAddress)}"] = "127.0.0.1",
        });
        builder.UseOrleans(silo => Program.Configure(silo, builder.Configuration));
        using var host = builder.Build();
        await host.StartAsync(ct);

        Assert.Null(await host.Services.GetRequiredService<IGrainFactory>().GetGrain<ISequencerGrain>("p").Get());
        // No module, kernel or brain service is composed (the grain classes themselves are TrustCompositionFacts').
        Assert.Null(host.Services.GetService<DigitalBrain.Framework.Modules.ModuleInventory>());
        Assert.Null(host.Services.GetService<IDigitalBrain>());
        Assert.DoesNotContain(host.Services.GetServices<IStartupTask>(), task => task.GetType().Assembly != typeof(Platform.TrustHosting).Assembly);
        // R66: it advertises and listens on loopback.
        Assert.Equal(System.Net.IPAddress.Loopback, host.Services.GetRequiredService<global::Microsoft.Extensions.Options.IOptions<Orleans.Configuration.EndpointOptions>>().Value.AdvertisedIPAddress);
        await host.StopAsync(ct);
    }

    // R105: a single-silo cluster restarts over a membership table whose previous run of the silo is still Active
    // (killed, on other ports): the startup connectivity check skips it once its heartbeat is older than the window.
    [Fact(Timeout = 120_000)]
    public async Task TheTrustHostRestartsOverAStaleActiveRowOfItsPreviousRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;
        var stale = new MembershipEntry
        {
            SiloAddress = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, Free()), 1),
            Status = SiloStatus.Active, HostName = "previous-run", SiloName = "previous-run", RoleName = "trust",
            StartTime = now - TimeSpan.FromMinutes(2),
            IAmAliveTime = now - (Platform.SingleSiloCluster.HeartbeatEvery * (Platform.SingleSiloCluster.MissedHeartbeats + 1)),
            SuspectTimes = [],
        };
        var table = new SeededMembershipTable(stale);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{Platform.Trust.TrustOptions.Key}:{nameof(Platform.Trust.TrustOptions.SiloPort)}"] = Free().ToString(System.Globalization.CultureInfo.InvariantCulture),
            [$"{Platform.Trust.TrustOptions.Key}:{nameof(Platform.Trust.TrustOptions.IdentityPath)}"] = Path.Combine(Path.GetTempPath(), $"trust-{Guid.NewGuid():N}", "identity.key"),
            [$"{Platform.Trust.TrustOptions.Key}:{nameof(Platform.Trust.TrustOptions.AdvertisedAddress)}"] = "127.0.0.1",
        });
        builder.UseOrleans(silo =>
        {
            Program.Configure(silo, builder.Configuration);
            silo.Services.AddSingleton<IMembershipTable>(table);
        });
        using var host = builder.Build();

        await host.StartAsync(ct);

        Assert.Null(await host.Services.GetRequiredService<IGrainFactory>().GetGrain<ISequencerGrain>("p").Get());
        var membership = host.Services.GetRequiredService<global::Microsoft.Extensions.Options.IOptions<Orleans.Configuration.ClusterMembershipOptions>>().Value;
        Assert.Equal(Platform.SingleSiloCluster.HeartbeatEvery, membership.IAmAliveTablePublishTimeout);
        await host.StopAsync(ct);
    }

    private static int Free()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}

// A membership table that outlives a silo, as Azurite's does across `aspire run`s, seeded with rows of earlier runs.
internal sealed class SeededMembershipTable : IMembershipTable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<SiloAddress, (MembershipEntry Entry, string ETag)> _rows = [];
    private TableVersion _version = new(0, "0");
    private int _etags;

    public SeededMembershipTable(params MembershipEntry[] seeded)
    {
        foreach (var entry in seeded) { _rows[entry.SiloAddress] = (entry, NextETag()); }
    }

    private string NextETag() => (++_etags).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public Task InitializeMembershipTable(bool tryInitTableVersion) => Task.CompletedTask;

    public Task DeleteMembershipTableEntries(string clusterId)
    {
        lock (_gate) { _rows.Clear(); }
        return Task.CompletedTask;
    }

    public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
    {
        lock (_gate)
        {
            foreach (var (address, row) in _rows.ToArray())
            {
                if (row.Entry.Status == SiloStatus.Dead && row.Entry.IAmAliveTime < beforeDate.UtcDateTime) { _rows.Remove(address); }
            }
        }
        return Task.CompletedTask;
    }

    public Task<MembershipTableData> ReadRow(SiloAddress key)
    {
        lock (_gate)
        {
            List<Tuple<MembershipEntry, string>> rows = _rows.TryGetValue(key, out var row) ? [Tuple.Create(row.Entry, row.ETag)] : [];
            return Task.FromResult(new MembershipTableData(rows, _version));
        }
    }

    public Task<MembershipTableData> ReadAll()
    {
        lock (_gate) { return Task.FromResult(new MembershipTableData([.. _rows.Values.Select(row => Tuple.Create(row.Entry, row.ETag))], _version)); }
    }

    public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
    {
        lock (_gate)
        {
            if (_rows.ContainsKey(entry.SiloAddress) || tableVersion.Version != _version.Version + 1) { return Task.FromResult(false); }
            _rows[entry.SiloAddress] = (entry, NextETag());
            _version = tableVersion;
            return Task.FromResult(true);
        }
    }

    public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(entry.SiloAddress, out var row) || row.ETag != etag || tableVersion.Version != _version.Version + 1) { return Task.FromResult(false); }
            _rows[entry.SiloAddress] = (entry, NextETag());
            _version = tableVersion;
            return Task.FromResult(true);
        }
    }

    public Task UpdateIAmAlive(MembershipEntry entry)
    {
        lock (_gate)
        {
            if (_rows.TryGetValue(entry.SiloAddress, out var row))
            {
                row.Entry.IAmAliveTime = entry.IAmAliveTime;
                _rows[entry.SiloAddress] = (row.Entry, NextETag());
            }
        }
        return Task.CompletedTask;
    }
}
