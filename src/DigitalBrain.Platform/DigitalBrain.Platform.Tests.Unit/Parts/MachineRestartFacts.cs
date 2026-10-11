using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

// R105: a machine silo is a single-silo cluster; its previous run's stale Active row is skipped by the startup
// connectivity check once the row's heartbeat is older than the window (with Orleans' defaults the start hangs).
public sealed class MachineRestartFacts
{
    [Fact(Timeout = 120_000)]
    public async Task AMachineRestartsOverAStaleActiveRowOfItsPreviousRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;
        using var unused = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        unused.Start();
        var port = ((System.Net.IPEndPoint)unused.LocalEndpoint).Port;
        unused.Stop();
        var table = new SeededMembershipTable(new MembershipEntry
        {
            SiloAddress = SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port), 1),
            Status = SiloStatus.Active, HostName = "previous-run", SiloName = "previous-run", RoleName = "machine",
            StartTime = now - TimeSpan.FromMinutes(2),
            IAmAliveTime = now - (SingleSiloCluster.HeartbeatEvery * (SingleSiloCluster.MissedHeartbeats + 1)),
            SuspectTimes = [],
        });

        await using var machine = await TestBrain.Create().WithPartLog()
            .ConfigureSilo(silo => silo.Services.AddSingleton<IMembershipTable>(table))
            .StartAsync(ct);

        var membership = machine.SiloServices.GetRequiredService<IOptions<ClusterMembershipOptions>>().Value;
        Assert.Equal(SingleSiloCluster.HeartbeatEvery, membership.IAmAliveTablePublishTimeout);
        Assert.Same(table, machine.SiloServices.GetRequiredService<IMembershipTable>());
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
