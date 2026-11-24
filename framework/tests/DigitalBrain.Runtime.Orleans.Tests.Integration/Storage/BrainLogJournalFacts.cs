using DigitalBrain.Kernel;
using DigitalBrain.Runtime.Orleans.Storage;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration.Storage;

internal sealed class BrainLogJournalTarget : IJournalTarget
{
    private readonly TempHome _home;
    private BrainStore _store;
    private BrainLogJournal _journal;
    private BrainLog _log;

    private BrainLogJournalTarget(TempHome home, BrainStore store, BrainLogJournal journal, BrainLog log)
    {
        _home = home;
        _store = store;
        _journal = journal;
        _log = log;
    }

    public static async Task<BrainLogJournalTarget> CreateAsync()
    {
        var home = new TempHome();
        BrainStore? store = null;
        try
        {
            store = new BrainStore(home.Path);
            var log = await store.Log("b1");
            var history = store.History("b1");
            var journal = new BrainLogJournal(store.Logs, history, id => BrainStore.GrainOf(id).ToString());
            return new BrainLogJournalTarget(home, store, journal, log);
        }
        catch
        {
            if (store is not null) { await store.DisposeAsync(); }
            home.Dispose();
            throw;
        }
    }

    public IBrainJournal Journal => _journal;
    public IJournalView View => _log;

    public async Task Reopen()
    {
        await _store.Reopen();
        _log = await _store.Log("b1");
        var history = _store.History("b1");
        _journal = new BrainLogJournal(_store.Logs, history, id => BrainStore.GrainOf(id).ToString());
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _store.DisposeAsync();
        }
        finally
        {
            _home.Dispose();
        }
    }
}

public sealed class BrainLogJournalFacts : JournalConformance
{
    internal override async Task<IJournalTarget> CreateTargetAsync()
        => await BrainLogJournalTarget.CreateAsync();
}
