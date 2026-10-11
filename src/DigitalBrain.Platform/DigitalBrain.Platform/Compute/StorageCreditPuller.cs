using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Platform.Compute;

// T23-11, R60, R76, R113: storage credits are recorded trust-side; the core holder of any of an account's brains pulls
// them into the account's machine-side ledger on the short-lease renewal cadence. The ledger credits an entry id once
// and the acknowledge comes after, so a lost acknowledge or a second puller never credits twice on one ledger.
internal sealed partial class StorageCreditPuller(PartLogs logs, IKeyringClient keyring, INodeDirectoryClient directory, IGrainFactory grains,
    IOptions<PartsOptions> parts, [FromKeyedServices(MeshOptions.Key)] TimeProvider time, ILogger<StorageCreditPuller> logger) : IHostedService, IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _passing = new(1, 1);
    private Task _pulling = Task.CompletedTask;

    // Facts only: runs between crediting the ledger and acknowledging the trust side (a lost acknowledge).
    internal Func<Task>? BeforeAcknowledge { get; set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _pulling = Loop();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_stop.IsCancellationRequested) { await _stop.CancelAsync(); }
        await _pulling;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _stop.Dispose();
        _passing.Dispose();
    }

    private async Task Loop()
    {
        using var timer = new PeriodicTimer(parts.Value.ShortLease - parts.Value.RenewBefore, time);
        try { while (await timer.WaitForNextTickAsync(_stop.Token)) { await Pass(_stop.Token); } }
        catch (OperationCanceledException) { }
    }

    internal async Task Pass(CancellationToken cancellationToken)
    {
        await _passing.WaitAsync(cancellationToken);
        try
        {
            var accounts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var core in logs.Opened.Where(part => part.IsCore).Select(part => part.BrainId).Distinct(StringComparer.Ordinal))
            {
                try { if (await keyring.OwnerOf(core) is { } account) { accounts.Add(account); } }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                { Log.PullFailed(logger, error, core); }
            }
            foreach (var account in accounts)
            {
                try { await Pull(account); }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                { Log.PullFailed(logger, error, account); }
            }
        }
        finally { _passing.Release(); }
    }

    private async Task Pull(string account)
    {
        var pending = await directory.PendingCredits(account);
        if (pending.Length == 0) { return; }
        var ledger = grains.GetGrain<ILedgerGrain>(account);
        var credited = 0;
        foreach (var credit in pending) { if (await ledger.Credit(credit.EntryId, credit.Micros, credit.Reason)) { credited++; } }
        if (BeforeAcknowledge is { } before) { await before(); }
        await directory.AcknowledgeCredits(account, [.. pending.Select(credit => credit.EntryId)]);
        Log.Pulled(logger, pending.Length, credited, account);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Pulled {Pending} storage credits for account {Account}; {Credited} were new to its ledger.")]
        public static partial void Pulled(ILogger logger, int pending, int credited, string account);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Storage credits for {Subject} could not be pulled this pass.")]
        public static partial void PullFailed(ILogger logger, Exception error, string subject);
    }
}
